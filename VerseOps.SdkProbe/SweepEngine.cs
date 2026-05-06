using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;

namespace VerseOps.SdkProbe;

/// <summary>
/// Walks the Microsoft.PowerPlatform.Management ServiceClient via reflection and invokes
/// every reachable verb method:
///   Pass 1 = parameterless GetAsync (lists + readonly tenant data) â€” no inputs.
///   Pass 2 = per-item GetAsync using ids harvested from Pass 1, **typed** to the parent
///            collection so an envId never gets passed to a policy indexer.
///   Pass 3 (future) = CRUD with explicit bodies.
///
/// Per-call extras:
///   - userId / tenantId / environmentId are injected into the request config's
///     QueryParameters (when those properties exist) â€” fixes most "HTTP 400 missing param".
///   - SeaCass env id is pinned at the front of the environment pool so calls are
///     deterministic against an env the operator owns.
/// </summary>
public sealed class SweepEngine
{
    private readonly object _serviceClient;
    private readonly string _outputPath;
    private readonly string? _userId;
    private readonly string? _tenantId;
    private readonly string? _pinnedEnvId;

    private readonly List<OpResult> _results = new();
    /// <summary>parent-builder-type-name -> list of ids harvested from its successful GetAsync items.</summary>
    private readonly Dictionary<string, List<string>> _idsByParentBuilder = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visitedPaths = new(StringComparer.Ordinal);

    /// <summary>Captured from the first successful CloudFlows list response. Filled into
    /// QueryParameters.WorkflowId on routes that demand it (e.g. FlowRuns).</summary>
    private string? _workflowId;

    /// <summary>Per-route timeout overrides keyed by a substring of the path. Default is 15s;
    /// these routes are known to be slow on the server side.</summary>
    private static readonly (string PathContains, TimeSpan Timeout)[] SlowRoutes = new[]
    {
        ("BusinessContinuityStateFullSnapshot", TimeSpan.FromSeconds(120)),
    };

    public SweepEngine(object serviceClient, string outputPath, string? userId, string? tenantId, string? pinnedEnvId)
    {
        _serviceClient = serviceClient;
        _outputPath = outputPath;
        _userId = userId;
        _tenantId = tenantId;
        _pinnedEnvId = pinnedEnvId;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== PASS 1: parameterless GetAsync (lists, tenant data) ===");
        Console.WriteLine();
        await WalkAsync(_serviceClient, "ServiceClient", depth: 0, maxDepth: 3, allowIndexers: false, ct);

        Console.WriteLine();
        Console.WriteLine("=== Harvested ids by parent builder ===");
        foreach (var kv in _idsByParentBuilder.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,-40} {kv.Value.Count} ids: {string.Join(", ", kv.Value.Take(3).Select(Short))}{(kv.Value.Count > 3 ? $" (+{kv.Value.Count - 3})" : "")}");

        if (_idsByParentBuilder.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== PASS 2: per-item GetAsync (typed id pool) ===");
            Console.WriteLine();
            _visitedPaths.Clear();
            await WalkAsync(_serviceClient, "ServiceClient", depth: 0, maxDepth: 4, allowIndexers: true, ct);
        }

        await WriteJsonAsync(ct);

        Console.WriteLine();
        var ok = _results.Count(r => r.Ok);
        var fail = _results.Count - ok;
        Console.WriteLine($"=== SUMMARY ===   ok={ok}   fail={fail}   total={_results.Count}");
        Console.WriteLine($"results saved to {_outputPath}");
    }

    // ------------------------------------------------------------------ walk

    private async Task WalkAsync(object node, string path, int depth, int maxDepth, bool allowIndexers, CancellationToken ct)
    {
        if (node is null || depth > maxDepth) return;
        if (!_visitedPaths.Add(path)) return;
        var t = node.GetType();

        await TryGetAsync(node, path, ct);

        // Recurse into nested non-indexed RequestBuilder properties.
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                     && p.GetIndexParameters().Length == 0
                     && IsBuilder(p.PropertyType)))
        {
            object? child = null;
            try { child = prop.GetValue(node); } catch { }
            if (child != null) await WalkAsync(child, $"{path}.{prop.Name}", depth + 1, maxDepth, allowIndexers, ct);
        }

        if (!allowIndexers) return;

        // For each indexer property, draw ids ONLY from the pool we tagged with this builder's
        // own type name (i.e. ids that came from a GetAsync on this very builder).
        foreach (var idx in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                     && p.GetIndexParameters().Length == 1
                     && p.GetIndexParameters()[0].ParameterType == typeof(string)
                     && IsBuilder(p.PropertyType)))
        {
            var keys = PickIdsForParent(t.Name, max: 1).ToList();
            // Special case: indexer of a builder we never successfully GETed - skip silently.
            if (keys.Count == 0) continue;

            foreach (var key in keys)
            {
                object? child;
                try { child = idx.GetValue(node, new object[] { key }); } catch { continue; }
                if (child is null) continue;
                await WalkAsync(child, $"{path}[{Short(key)}]", depth + 1, maxDepth, allowIndexers: true, ct);
            }
        }
    }

    // ------------------------------------------------------------------ invoke

    private async Task TryGetAsync(object builder, string path, CancellationToken ct)
    {
        var t = builder.GetType();
        var getAsync = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "GetAsync")
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault(m => m.GetParameters().All(p => p.HasDefaultValue
                || p.ParameterType == typeof(CancellationToken)
                || (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))));
        if (getAsync is null) return;

        var args = getAsync.GetParameters().Select<ParameterInfo, object?>(p =>
        {
            if (p.ParameterType == typeof(CancellationToken)) return ct;
            if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))
                return BuildRequestConfigAction(p.ParameterType);
            return p.HasDefaultValue ? p.DefaultValue : null;
        }).ToArray();

        Console.Write($"  GET {path,-90} ");
        // Per-call timeout (15s default; longer for known-slow routes) so a single hung
        // route doesn't burn 100s on the default HttpClient.
        var timeout = TimeSpan.FromSeconds(15);
        foreach (var (frag, t2) in SlowRoutes)
            if (path.Contains(frag, StringComparison.Ordinal)) { timeout = t2; break; }
        using var perCall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCall.CancelAfter(timeout);
        var perCallArgs = (object?[])args.Clone();
        for (int i = 0; i < perCallArgs.Length; i++)
            if (perCallArgs[i] is CancellationToken) perCallArgs[i] = perCall.Token;
        ErrorBodyCaptureHandler.Reset();
        try
        {
            var task = (Task)getAsync.Invoke(builder, perCallArgs)!;
            await task.ConfigureAwait(false);
            var resp = task.GetType().GetProperty("Result")?.GetValue(task);
            var summary = Summarise(resp);
            Console.WriteLine($"OK   {summary}");
            _results.Add(new OpResult(path, "GET", true, 0, summary, SafeSerialize(resp), null));
            HarvestIds(t.Name, resp);
            FlushIncremental();
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ReportFailure(path, tie.InnerException);
        }
        catch (Exception ex)
        {
            ReportFailure(path, ex);
        }
    }

    private void ReportFailure(string path, Exception ex)
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
        _results.Add(new OpResult(path, "GET", false, status, null, null, error));
        FlushIncremental();
    }

    // ------------------------------------------------------------------ request config (userId/tenantId/envId)

    /// <summary>Builds an Action&lt;TConfig&gt; that fills QueryParameters with values the server
    /// commonly demands. We only ever set properties that the SDK already exposes â€” we don't
    /// add unknown query keys, we don't hand-craft URLs. Reflective so we cover all routes
    /// without per-namespace code. Discovered QP shapes are in sdk-qp-shapes.txt
    /// (regenerate via `--inspect-qps`).
    ///
    /// Filled (when the SDK actually exposes a settable property of the matching type):
    ///   - Path-style ids as String or Guid?: "UserId", "TenantId", "EnvironmentId",
    ///     "Environment", "EnvironmentName", "WorkflowId", "FlowId".
    ///   - Bool? "IncludeRuleSetCounts" = false  (RuleBasedPolicies.Assignments).
    ///   - DateTime/DateTimeOffset "StartDate"/"EndDate" = (UtcNow-7d, UtcNow)  (UserPerFlow).
    ///   - String "Filter" = "environment eq '&lt;pinnedEnvId&gt;'"  (Connectors $filter
    ///     server-side requirement).
    /// We intentionally do NOT inject OwnerId / CreatedBy / ResourceId â€” those are
    /// filter-style QPs that, when set, can return an empty list (e.g. flows the current
    /// user doesn't own) and break downstream id harvesting.
    /// </summary>
    private object? BuildRequestConfigAction(Type actionGenericType)
    {
        // actionGenericType = Action<TRequestConfiguration>
        var configType = actionGenericType.GetGenericArguments()[0];
        var qpProp = configType.GetProperty("QueryParameters", BindingFlags.Public | BindingFlags.Instance);
        if (qpProp is null) return null;
        var qpType = qpProp.PropertyType;

        var startDate = DateTime.UtcNow.AddDays(-7);
        var endDate   = DateTime.UtcNow;

        // Candidate (PropertyName, StringValue) â€” we'll try this value as either string or Guid?
        // depending on what the SDK exposes. Order matters; first match wins per property name.
        // We deliberately do NOT include OwnerId / CreatedBy / ResourceId here: those are
        // filter-style QPs and setting them on list endpoints (e.g. CloudFlows) returns an
        // empty result if the current user happens not to own anything, which then breaks
        // downstream id harvesting.
        var idCandidates = new (string Name, string? Value)[]
        {
            ("UserId", _userId),
            ("TenantId", _tenantId),
            ("EnvironmentId", _pinnedEnvId),
            ("Environment", _pinnedEnvId),       // some SDK QPs use this name
            ("EnvironmentName", _pinnedEnvId),
            ("WorkflowId", _workflowId),
            ("FlowId", _workflowId),
        };
        var boolCandidates = new (string Name, bool Value)[]
        {
            ("IncludeRuleSetCounts", false),
        };

        // Build OData $filter for routes that demand it (e.g. Connectors's "MissingEnvironmentFilter").
        var filterValue = !string.IsNullOrEmpty(_pinnedEnvId)
            ? $"environment eq '{_pinnedEnvId}'"
            : null;

        var assigns = new List<System.Linq.Expressions.Expression>();
        var pCfg = System.Linq.Expressions.Expression.Parameter(configType, "cfg");
        var pQp  = System.Linq.Expressions.Expression.Property(pCfg, qpProp);

        void AddAssign(PropertyInfo p, object value, Type valueType)
        {
            var prop   = System.Linq.Expressions.Expression.Property(pQp, p);
            var konst  = System.Linq.Expressions.Expression.Constant(value, valueType);
            System.Linq.Expressions.Expression rhs = konst;
            if (p.PropertyType != valueType)
                rhs = System.Linq.Expressions.Expression.Convert(konst, p.PropertyType);
            assigns.Add(System.Linq.Expressions.Expression.Assign(prop, rhs));
        }

        // ---- id candidates: try string, fall back to Guid? if the SDK uses Nullable<Guid>
        foreach (var (name, value) in idCandidates)
        {
            if (string.IsNullOrEmpty(value)) continue;
            var p = qpType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) continue;
            var nonNullable = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (nonNullable == typeof(string))
            {
                AddAssign(p, value, typeof(string));
            }
            else if (nonNullable == typeof(Guid) && Guid.TryParse(value, out var g))
            {
                AddAssign(p, g, typeof(Guid));
            }
        }
        // ---- $filter (OData) for routes that need an env filter even when env is in the path
        if (filterValue != null)
        {
            var pf = qpType.GetProperty("Filter", BindingFlags.Public | BindingFlags.Instance);
            if (pf != null && pf.CanWrite && pf.PropertyType == typeof(string))
                AddAssign(pf, filterValue, typeof(string));
        }
        // ---- bool / bool? properties
        foreach (var (name, value) in boolCandidates)
        {
            var p = qpType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) continue;
            var nonNullable = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (nonNullable != typeof(bool)) continue;
            AddAssign(p, value, typeof(bool));
        }
        // ---- DateTime / DateTimeOffset / nullable variants for date range filters
        foreach (var (name, value) in new (string Name, DateTime Value)[] { ("StartDate", startDate), ("EndDate", endDate) })
        {
            var p = qpType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) continue;
            var nonNullable = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (nonNullable == typeof(DateTime))
                AddAssign(p, value, typeof(DateTime));
            else if (nonNullable == typeof(DateTimeOffset))
                AddAssign(p, new DateTimeOffset(value, TimeSpan.Zero), typeof(DateTimeOffset));
            else if (nonNullable == typeof(string))
                AddAssign(p, value.ToString("yyyy-MM-ddTHH:mm:ssZ"), typeof(string));
        }

        if (assigns.Count == 0) return null;
        var lambda = System.Linq.Expressions.Expression.Lambda(actionGenericType,
            System.Linq.Expressions.Expression.Block(assigns), pCfg);
        return lambda.Compile();
    }

    // ------------------------------------------------------------------ flush

    private DateTime _lastFlush = DateTime.MinValue;
    private void FlushIncremental()
    {
        if ((DateTime.UtcNow - _lastFlush).TotalSeconds < 1) return;
        _lastFlush = DateTime.UtcNow;
        try
        {
            var doc = new
            {
                generatedUtc = DateTime.UtcNow,
                inProgress = true,
                userId = _userId,
                tenantId = _tenantId,
                pinnedEnvironmentId = _pinnedEnvId,
                idsByParentBuilder = _idsByParentBuilder.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value),
                results = _results
            };
            File.WriteAllText(_outputPath,
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsBuilder(Type t)
        => t.Name.EndsWith("RequestBuilder", StringComparison.Ordinal)
           && t.Namespace?.StartsWith("Microsoft.PowerPlatform.Management", StringComparison.Ordinal) == true;

    private static string Short(string id) => id.Length > 8 ? id[..8] + "..." : id;

    private static string Summarise(object? response)
    {
        if (response is null) return "(null)";
        var t = response.GetType();
        var v = t.GetProperty("Value")?.GetValue(response);
        if (v is ICollection col) return $"{t.Name}: {col.Count} items";
        if (v is IEnumerable e) { int n = 0; foreach (var _ in e) n++; return $"{t.Name}: {n} items"; }
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

    /// <summary>Walks response.Value, pulls Id/Name strings from each item, stores them keyed
    /// by the builder type that produced the response. Pinned env id is prepended when this
    /// is the environment list.</summary>
    private void HarvestIds(string parentBuilderTypeName, object? response)
    {
        if (response is null) return;
        var t = response.GetType();
        var values = t.GetProperty("Value")?.GetValue(response);
        IEnumerable<object>? items = values switch
        {
            IEnumerable<object> oe => oe,
            IEnumerable e => e.Cast<object>(),
            _ => null
        };
        if (items is null) return;

        if (!_idsByParentBuilder.TryGetValue(parentBuilderTypeName, out var pool))
            _idsByParentBuilder[parentBuilderTypeName] = pool = new List<string>();

        // For environments specifically, pin SeaCass at the front.
        if (parentBuilderTypeName == "EnvironmentsRequestBuilder" && !string.IsNullOrEmpty(_pinnedEnvId)
            && !pool.Contains(_pinnedEnvId))
            pool.Add(_pinnedEnvId);

        int taken = 0;
        foreach (var item in items)
        {
            if (item is null) continue;
            var idLike = ExtractPrimaryId(item);
            if (idLike != null && !pool.Contains(idLike)) pool.Add(idLike);
            // Capture a workflow id (Guid) from the cloud-flows list so FlowRuns can satisfy
            // its "WorkflowId is a required filter" demand using the SDK's own QP property.
            // CloudFlow items expose both Name (display) and WorkflowId (Guid) â€” we must
            // grab the Guid explicitly, since ExtractPrimaryId picks Name first.
            if (_workflowId is null && parentBuilderTypeName == "CloudFlowsRequestBuilder")
            {
                var wfProp = item.GetType().GetProperty("WorkflowId", BindingFlags.Public | BindingFlags.Instance);
                if (wfProp != null)
                {
                    var nn = Nullable.GetUnderlyingType(wfProp.PropertyType) ?? wfProp.PropertyType;
                    var v  = wfProp.GetValue(item);
                    if (nn == typeof(Guid) && v is Guid g && g != Guid.Empty) _workflowId = g.ToString();
                    else if (nn == typeof(string) && v is string s && Guid.TryParse(s, out var g2)) _workflowId = g2.ToString();
                }
            }
            if (++taken >= 5) break; // 5 per collection is plenty
        }
    }

    /// <summary>Pick the most appropriate identifier from an item: prefer "Id" (most PPAC item
    /// routes use it as the path key), then "Name", then typed *Id properties.
    /// "WorkflowId" is included so CloudFlow harvest yields a guid (CloudFlow items don't
    /// expose Id/Name; their primary id is WorkflowId).</summary>
    private static string? ExtractPrimaryId(object item)
    {
        var t = item.GetType();
        string[] preferred = { "Id", "Name", "WorkflowId", "EnvironmentId", "GroupId", "PolicyId", "BillingPolicyId", "WebsiteId", "OperationId" };
        foreach (var name in preferred)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) continue;
            // Accept either string or Guid? â€” the SDK uses both.
            if (p.PropertyType == typeof(string))
            {
                if (p.GetValue(item) is string s && !string.IsNullOrWhiteSpace(s)) return s;
            }
            else
            {
                var nn = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (nn == typeof(Guid))
                {
                    var v = p.GetValue(item);
                    if (v is Guid g && g != Guid.Empty) return g.ToString();
                }
            }
        }
        return null;
    }

    private IEnumerable<string> PickIdsForParent(string parentBuilderTypeName, int max)
    {
        if (_idsByParentBuilder.TryGetValue(parentBuilderTypeName, out var list) && list.Count > 0)
            return list.Take(max);
        return Array.Empty<string>();
    }

    private async Task WriteJsonAsync(CancellationToken ct)
    {
        var doc = new
        {
            generatedUtc = DateTime.UtcNow,
            userId = _userId,
            tenantId = _tenantId,
            pinnedEnvironmentId = _pinnedEnvId,
            idsByParentBuilder = _idsByParentBuilder.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value),
            results = _results
        };
        await File.WriteAllTextAsync(_outputPath,
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
}

public sealed record OpResult(string Path, string Verb, bool Ok, int HttpStatus, string? Summary, string? Sample, string? Error);
