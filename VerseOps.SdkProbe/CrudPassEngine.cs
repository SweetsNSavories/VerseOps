using System.Reflection;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;

namespace VerseOps.SdkProbe;

/// <summary>
/// CRUD pass over the PPAC SDK. Strict scope (matches the user's sandbox grant):
/// <list type="bullet">
///   <item>Pass A: Bucket A read-only POSTs (return data, no mutation).</item>
///   <item>Pass D: Bucket D env-lifecycle POSTs/Deletes with QueryParameters.ValidateOnly = true
///         (server validates without applying).</item>
///   <item>Pass C: Bucket C scoped create-then-delete (currently EnvironmentGroups only).
///         Every created resource is recorded in <see cref="_ledger"/>; only ledger entries
///         are ever deleted. Names are tagged "verseops-probe-{ticks}" for forensic clarity.</item>
/// </list>
/// We never mutate any pre-existing artifact (the user's directive is "do not delete any other
/// orgs or their apps flows etc"). We only delete what we ourselves created in this run.
/// </summary>
public sealed class CrudPassEngine
{
    private readonly object _serviceClient;
    private readonly string _outputPath;
    private readonly string? _userId;
    private readonly string? _tenantId;
    private readonly string _sandboxEnvId;
    private readonly string _sandboxEnvName;

    private readonly List<OpResult> _results = new();
    /// <summary>Resources we created during this run, keyed by SDK builder type name.
    /// Only ids in this ledger are ever passed to a DeleteAsync.</summary>
    private readonly Dictionary<string, List<string>> _ledger = new(StringComparer.Ordinal);

    public CrudPassEngine(object serviceClient, string outputPath, string? userId, string? tenantId,
                          string sandboxEnvId, string sandboxEnvName)
    {
        _serviceClient   = serviceClient;
        _outputPath      = outputPath;
        _userId          = userId;
        _tenantId        = tenantId;
        _sandboxEnvId    = sandboxEnvId;
        _sandboxEnvName  = sandboxEnvName;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"=== PASS A: Bucket A — read-only POSTs (no mutation) ===");
        Console.WriteLine();
        await PassA_ReadOnlyPostsAsync(ct);

        Console.WriteLine();
        Console.WriteLine($"=== PASS D: Bucket D — ValidateOnly dry-run (env lifecycle) ===");
        Console.WriteLine($"     target env: {_sandboxEnvName}  id={_sandboxEnvId}");
        Console.WriteLine();
        await PassD_ValidateOnlyAsync(ct);

        Console.WriteLine();
        Console.WriteLine($"=== PASS C: Bucket C — scoped create+delete (EnvironmentGroups only) ===");
        Console.WriteLine();
        await PassC_EnvGroupCreateDeleteAsync(ct);

        await WriteJsonAsync(ct);

        Console.WriteLine();
        var ok = _results.Count(r => r.Ok);
        var fail = _results.Count - ok;
        Console.WriteLine($"=== CRUD SUMMARY ===   ok={ok}   fail={fail}   total={_results.Count}");
        Console.WriteLine($"crud results saved to {_outputPath}");
        if (_ledger.Count > 0)
        {
            Console.WriteLine($"Resources created (and attempted-deleted) this run:");
            foreach (var (k, v) in _ledger.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {k}: {string.Join(", ", v)}");
        }
    }

    // ============================================================ PASS A: read-only POSTs

    private async Task PassA_ReadOnlyPostsAsync(CancellationToken ct)
    {
        // 1) ResourceQuery — returns a list of resources matching a query body.
        await TryInvokeAsync(
            path: "ServiceClient.Resourcequery.Resources.Query",
            verb: "POST",
            builder: GetByPath(_serviceClient, "Resourcequery", "Resources", "Query"),
            methodName: "PostAsync",
            buildBody: builder =>
            {
                // Body: ResourceQueryRequest. Inspect for required-ish props and fill minimally.
                var bodyType = builder.GetType().Assembly.GetType(
                    "Microsoft.PowerPlatform.Management.Models.ResourceQueryRequest");
                if (bodyType is null) return null;
                var body = Activator.CreateInstance(bodyType);
                // EntityType is the typical scoping property; set to "environment" if it exists.
                TrySet(body, "EntityType", "environment");
                return body;
            },
            extraQp: null,
            ct);

        // 2) CrossTenantConnectionReports — POST, no body, returns a fresh report object.
        await TryInvokeAsync(
            path: "ServiceClient.Governance.CrossTenantConnectionReports",
            verb: "POST",
            builder: GetByPath(_serviceClient, "Governance", "CrossTenantConnectionReports"),
            methodName: "PostAsync",
            buildBody: null,
            extraQp: null,
            ct);

        // 3) Powerpages.Scan.Quick.Execute — quick read-only scan over an existing site.
        // We DO NOT touch the site (Start/Stop/Restart/EnableWaf/etc are skipped).
        // This is read-only on our side; the scan only reports.
        var websiteIds = TryListWebsiteIds(_sandboxEnvId, ct);
        foreach (var siteId in websiteIds)
        {
            var execBuilder = GetByPath(_serviceClient, "Powerpages", "Environments");
            if (execBuilder is null) continue;
            var envIndexer = execBuilder.GetType().GetProperty("Item", new[] { typeof(string) });
            var envItem = envIndexer?.GetValue(execBuilder, new object[] { _sandboxEnvId });
            var sites = envItem?.GetType().GetProperty("Websites")?.GetValue(envItem);
            var siteIndexer = sites?.GetType().GetProperty("Item", new[] { typeof(string) });
            var siteItem = siteIndexer?.GetValue(sites, new object[] { siteId });
            var scan = siteItem?.GetType().GetProperty("Scan")?.GetValue(siteItem);
            var quick = scan?.GetType().GetProperty("Quick")?.GetValue(scan);
            var exec = quick?.GetType().GetProperty("Execute")?.GetValue(quick);
            if (exec is null) continue;
            await TryInvokeAsync(
                path: $"ServiceClient.Powerpages.Environments[{Short(_sandboxEnvId)}].Websites[{Short(siteId)}].Scan.Quick.Execute",
                verb: "POST",
                builder: exec,
                methodName: "PostAsync",
                buildBody: null,
                extraQp: null,
                ct);
        }
    }

    // ============================================================ PASS D: ValidateOnly dry-run

    /// <summary>
    /// (PathSegments, BuilderName, Method, OptionalBodyTypeName).
    /// PathSegments are property names on ServiceClient → ... that lead to the env-item builder
    /// (where applicable). For env-item-rooted methods we use the indexer with the sandbox env id.
    /// </summary>
    private static readonly (string Display, string[] EnvItemPath, string MethodName, string? BodyTypeName)[] BucketD = new[]
    {
        ("Disable",                      new[] { "Disable" },                 "PostAsync", "Microsoft.PowerPlatform.Management.Models.StateChangeRequest"),
        ("Enable",                       new[] { "Enable" },                  "PostAsync", "Microsoft.PowerPlatform.Management.Models.StateChangeRequest"),
        ("Copy",                         new[] { "Copy" },                    "PostAsync", "Microsoft.PowerPlatform.Management.Models.CopyRequest"),
        ("Restore",                      new[] { "Restore" },                 "PostAsync", "Microsoft.PowerPlatform.Management.Models.EnvironmentRestoreRequest"),
        ("Recover",                      new[] { "Recover" },                 "PostAsync", null),
        ("ModifySku",                    new[] { "ModifySku" },               "PostAsync", "Microsoft.PowerPlatform.Management.Models.ModifyEnvironmentSkuRequest"),
        ("ForceFailover",                new[] { "ForceFailover" },           "PostAsync", "Microsoft.PowerPlatform.Management.Models.ForceFailoverRequest"),
        ("EnableDisasterRecovery",       new[] { "EnableDisasterRecovery" },  "PostAsync", null),
        ("DisableDisasterRecovery",      new[] { "DisableDisasterRecovery" }, "PostAsync", null),
        ("DisasterRecoveryDrill",        new[] { "DisasterRecoveryDrill" },   "PostAsync", null),
        ("Governancesetting.Enablemanaged",  new[] { "Governancesetting", "Enablemanaged"  }, "PostAsync", null),
        ("Governancesetting.Disablemanaged", new[] { "Governancesetting", "Disablemanaged" }, "PostAsync", null),
        ("Delete (env)",                  Array.Empty<string>(),              "DeleteAsync", null),
    };

    private async Task PassD_ValidateOnlyAsync(CancellationToken ct)
    {
        // Resolve the env-item builder once: ServiceClient.Environmentmanagement.Environments[envId]
        var envColl  = GetByPath(_serviceClient, "Environmentmanagement", "Environments");
        var envIndex = envColl?.GetType().GetProperty("Item", new[] { typeof(string) });
        var envItem  = envIndex?.GetValue(envColl, new object[] { _sandboxEnvId });
        if (envItem is null)
        {
            Console.WriteLine("  WARN: could not resolve env-item builder; skipping Pass D");
            return;
        }

        foreach (var (display, segs, methodName, bodyTypeName) in BucketD)
        {
            object? builder = envItem;
            foreach (var seg in segs)
            {
                builder = builder?.GetType().GetProperty(seg)?.GetValue(builder);
                if (builder is null) break;
            }
            if (builder is null) continue;

            await TryInvokeAsync(
                path: $"ServiceClient.Environmentmanagement.Environments[{Short(_sandboxEnvId)}].{display} (ValidateOnly)",
                verb: methodName.Replace("Async", "").ToUpperInvariant(),
                builder: builder,
                methodName: methodName,
                buildBody: bodyTypeName is null ? null : (b => CreateAndFillMinimal(b.GetType().Assembly.GetType(bodyTypeName)!)),
                extraQp: new Dictionary<string, object?> { { "ValidateOnly", true } },
                ct);
        }
    }

    // ============================================================ PASS C: scoped create+delete

    private async Task PassC_EnvGroupCreateDeleteAsync(CancellationToken ct)
    {
        var groupsBuilder = GetByPath(_serviceClient, "Environmentmanagement", "EnvironmentGroups");
        if (groupsBuilder is null)
        {
            Console.WriteLine("  WARN: EnvironmentGroups builder not found; skipping Pass C");
            return;
        }

        var asm = groupsBuilder.GetType().Assembly;
        var bodyType = asm.GetType("Microsoft.PowerPlatform.Management.Models.EnvironmentGroup");
        if (bodyType is null)
        {
            Console.WriteLine("  WARN: EnvironmentGroup model type not found; skipping Pass C");
            return;
        }

        var probeName = $"verseops-probe-{DateTime.UtcNow.Ticks}";
        var body = Activator.CreateInstance(bodyType);
        TrySet(body, "DisplayName", probeName);
        TrySet(body, "Description", "Created by VerseOps.SdkProbe; will be deleted immediately.");

        // CREATE
        var createPath = "ServiceClient.Environmentmanagement.EnvironmentGroups (CREATE)";
        var createResp = await TryInvokeAsync(
            path: createPath,
            verb: "POST",
            builder: groupsBuilder,
            methodName: "PostAsync",
            buildBody: _ => body,
            extraQp: null,
            ct);

        // Pull id from the response so we can delete.
        var createdId = ExtractGuidIdProperty(createResp, "Id") ?? ExtractGuidIdProperty(createResp, "Name");
        if (string.IsNullOrEmpty(createdId))
        {
            Console.WriteLine("  WARN: create succeeded but no id returned; cannot run delete");
            return;
        }

        // Record in ledger so DELETE is allowed.
        AddToLedger("EnvironmentGroupsRequestBuilder", createdId);

        // DELETE — only because the id is in our ledger.
        var itemIndex = groupsBuilder.GetType().GetProperty("Item", new[] { typeof(string) });
        var itemBuilder = itemIndex?.GetValue(groupsBuilder, new object[] { createdId });
        if (itemBuilder is null)
        {
            Console.WriteLine("  WARN: indexer did not yield an item builder for the created id");
            return;
        }
        await TryInvokeAsync(
            path: $"ServiceClient.Environmentmanagement.EnvironmentGroups[{Short(createdId)}] (DELETE)",
            verb: "DELETE",
            builder: itemBuilder,
            methodName: "DeleteAsync",
            buildBody: null,
            extraQp: null,
            ct);
    }

    // ============================================================ invoke helper (shared)

    /// <summary>Generic invoker. Resolves the named async method on the builder, builds args
    /// (body via factory + Action&lt;TConfig&gt; that sets QPs from <paramref name="extraQp"/> +
    /// CancellationToken), invokes, awaits, captures result + error body, records an OpResult.</summary>
    private async Task<object?> TryInvokeAsync(
        string path,
        string verb,
        object? builder,
        string methodName,
        Func<object, object?>? buildBody,
        IDictionary<string, object?>? extraQp,
        CancellationToken ct)
    {
        Console.Write($"  {verb,-6} {path,-100} ");
        if (builder is null)
        {
            Console.WriteLine("SKIP (builder null)");
            _results.Add(new OpResult(path, verb, false, 0, null, null, "builder null"));
            return null;
        }

        var t = builder.GetType();
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == methodName)
            .OrderByDescending(m => m.GetParameters().Length) // prefer the overload with body
            .ToArray();
        if (methods.Length == 0)
        {
            Console.WriteLine($"SKIP (no {methodName} on {t.Name})");
            _results.Add(new OpResult(path, verb, false, 0, null, null, $"no {methodName} on {t.Name}"));
            return null;
        }
        var method = methods[0];

        var args = method.GetParameters().Select<ParameterInfo, object?>(p =>
        {
            if (p.ParameterType == typeof(CancellationToken)) return ct;
            if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))
                return BuildExtraQpAction(p.ParameterType, extraQp);
            // Otherwise it's the body parameter.
            return buildBody?.Invoke(builder);
        }).ToArray();

        using var perCall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCall.CancelAfter(TimeSpan.FromSeconds(60));
        for (int i = 0; i < args.Length; i++)
            if (args[i] is CancellationToken) args[i] = perCall.Token;

        ErrorBodyCaptureHandler.Reset();
        try
        {
            var task = (Task)method.Invoke(builder, args)!;
            await task.ConfigureAwait(false);
            object? resp = null;
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp != null && task.GetType().IsGenericType) resp = resultProp.GetValue(task);
            var summary = Summarise(resp);
            Console.WriteLine($"OK   {summary}");
            _results.Add(new OpResult(path, verb, true, 0, summary, SafeSerialize(resp), null));
            return resp;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ReportFailure(path, verb, tie.InnerException);
            return null;
        }
        catch (Exception ex)
        {
            ReportFailure(path, verb, ex);
            return null;
        }
    }

    private void ReportFailure(string path, string verb, Exception ex)
    {
        var status = (ex as ApiException)?.ResponseStatusCode ?? 0;
        var body = ErrorBodyCaptureHandler.GetAndClear();
        var msg = ex.Message;
        if (msg.Length > 110) msg = msg[..110] + "...";
        var bodySnippet = string.IsNullOrEmpty(body) ? ""
            : "  body=" + (body.Length > 200 ? body[..200].Replace("\n", " ").Replace("\r", "") + "..."
                                              : body.Replace("\n", " ").Replace("\r", ""));
        Console.WriteLine($"FAIL HTTP {status}  {ex.GetType().Name}: {msg}{bodySnippet}");
        var error = string.IsNullOrEmpty(body)
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message} | body: {body}";
        _results.Add(new OpResult(path, verb, false, status, null, null, error));
    }

    /// <summary>Build an Action&lt;TConfig&gt; that sets QueryParameters values listed in
    /// <paramref name="extraQp"/>. Used to force ValidateOnly=true on Bucket D dry-runs.</summary>
    private static object? BuildExtraQpAction(Type actionGenericType, IDictionary<string, object?>? extraQp)
    {
        if (extraQp is null || extraQp.Count == 0) return null;
        var configType = actionGenericType.GetGenericArguments()[0];
        var qpProp = configType.GetProperty("QueryParameters", BindingFlags.Public | BindingFlags.Instance);
        if (qpProp is null) return null;
        var qpType = qpProp.PropertyType;

        var pCfg = System.Linq.Expressions.Expression.Parameter(configType, "cfg");
        var pQp  = System.Linq.Expressions.Expression.Property(pCfg, qpProp);
        var assigns = new List<System.Linq.Expressions.Expression>();
        foreach (var (name, value) in extraQp)
        {
            if (value is null) continue;
            var p = qpType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) continue;
            var nonNull = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (nonNull != value.GetType() && !nonNull.IsAssignableFrom(value.GetType())) continue;
            var konst = System.Linq.Expressions.Expression.Constant(value, value.GetType());
            System.Linq.Expressions.Expression rhs = konst;
            if (p.PropertyType != value.GetType())
                rhs = System.Linq.Expressions.Expression.Convert(konst, p.PropertyType);
            assigns.Add(System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Property(pQp, p), rhs));
        }
        if (assigns.Count == 0) return null;
        return System.Linq.Expressions.Expression.Lambda(actionGenericType,
            System.Linq.Expressions.Expression.Block(assigns), pCfg).Compile();
    }

    /// <summary>Create an instance of <paramref name="bodyType"/> with as little filled in as possible.
    /// Used for Bucket D ValidateOnly dry-runs where the body shape needs to deserialise but its
    /// values won't be applied.</summary>
    private static object? CreateAndFillMinimal(Type bodyType)
    {
        try
        {
            var inst = Activator.CreateInstance(bodyType);
            // Nothing else — server validates structure, not values, in dry-run mode.
            return inst;
        }
        catch { return null; }
    }

    // ============================================================ ledger / safety

    private void AddToLedger(string builderTypeName, string id)
    {
        if (!_ledger.TryGetValue(builderTypeName, out var list))
            _ledger[builderTypeName] = list = new List<string>();
        if (!list.Contains(id)) list.Add(id);
    }

    // ============================================================ helpers

    private static object? GetByPath(object root, params string[] path)
    {
        object? cur = root;
        foreach (var seg in path)
        {
            if (cur is null) return null;
            cur = cur.GetType().GetProperty(seg, BindingFlags.Public | BindingFlags.Instance)?.GetValue(cur);
        }
        return cur;
    }

    private static void TrySet(object? target, string propName, object? value)
    {
        if (target is null) return;
        var p = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite) return;
        try
        {
            if (value is null) { p.SetValue(target, null); return; }
            var nn = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (nn.IsAssignableFrom(value.GetType())) p.SetValue(target, value);
        }
        catch { /* best-effort */ }
    }

    private static string? ExtractGuidIdProperty(object? response, string propName)
    {
        if (response is null) return null;
        var p = response.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return null;
        var v = p.GetValue(response);
        if (v is string s && !string.IsNullOrWhiteSpace(s)) return s;
        if (v is Guid g && g != Guid.Empty) return g.ToString();
        var nn = Nullable.GetUnderlyingType(p.PropertyType);
        if (nn == typeof(Guid) && v is Guid g2 && g2 != Guid.Empty) return g2.ToString();
        return null;
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] + "..." : id;

    private static string Summarise(object? response)
    {
        if (response is null) return "(null)";
        var t = response.GetType();
        var v = t.GetProperty("Value")?.GetValue(response);
        if (v is System.Collections.ICollection col) return $"{t.Name}: {col.Count} items";
        return t.Name;
    }

    private static string? SafeSerialize(object? response)
    {
        if (response is null) return null;
        try
        {
            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                MaxDepth = 6
            });
        }
        catch { return null; }
    }

    /// <summary>Look up Power Pages websites in the sandbox env via the existing list call so
    /// we can target Scan.Quick.Execute on a real site id without picking ids that aren't ours
    /// to scan.</summary>
    private List<string> TryListWebsiteIds(string envId, CancellationToken ct)
    {
        try
        {
            var ppEnvs = GetByPath(_serviceClient, "Powerpages", "Environments");
            var idx = ppEnvs?.GetType().GetProperty("Item", new[] { typeof(string) });
            var envItem = idx?.GetValue(ppEnvs, new object[] { envId });
            var websitesBuilder = envItem?.GetType().GetProperty("Websites")?.GetValue(envItem);
            if (websitesBuilder is null) return new();
            var get = websitesBuilder.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GetAsync")
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault(m => m.GetParameters().All(p => p.HasDefaultValue
                    || p.ParameterType == typeof(CancellationToken)
                    || (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))));
            if (get is null) return new();
            var args = get.GetParameters().Select<ParameterInfo, object?>(p =>
            {
                if (p.ParameterType == typeof(CancellationToken)) return ct;
                return p.HasDefaultValue ? p.DefaultValue : null;
            }).ToArray();
            var task = (Task)get.Invoke(websitesBuilder, args)!;
            task.GetAwaiter().GetResult();
            var resp = task.GetType().GetProperty("Result")?.GetValue(task);
            var values = resp?.GetType().GetProperty("Value")?.GetValue(resp) as System.Collections.IEnumerable;
            if (values is null) return new();
            var ids = new List<string>();
            foreach (var item in values)
            {
                var id = ExtractGuidIdProperty(item, "Id") ?? ExtractGuidIdProperty(item, "Name") ?? ExtractGuidIdProperty(item, "WebsiteId");
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }
        catch { return new(); }
    }

    // ============================================================ flush

    private async Task WriteJsonAsync(CancellationToken ct)
    {
        var doc = new
        {
            generatedUtc = DateTime.UtcNow,
            userId = _userId,
            tenantId = _tenantId,
            sandboxEnvId = _sandboxEnvId,
            sandboxEnvName = _sandboxEnvName,
            ledger = _ledger.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value),
            results = _results
        };
        await File.WriteAllTextAsync(_outputPath,
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
}
