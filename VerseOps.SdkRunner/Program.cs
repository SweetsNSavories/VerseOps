using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.PowerPlatform.Management;
using VerseOps.SdkRunner;

// ============================================================================
// Configuration  (reuses VerseOps.Sample's user-secrets / appsettings keys)
// ============================================================================
var cfg = new ConfigurationBuilder()
    .AddJsonFile(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VerseOps.Sample", "appsettings.json")),
        optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

string Get(string key) => cfg[key]
    ?? Environment.GetEnvironmentVariable(key.Replace(":", "__"))
    ?? string.Empty;

var tenantId     = Get("PowerPlatform:TenantId");
var clientId     = Get("PowerPlatform:ClientId");
var clientSecret = Get("PowerPlatform:ClientSecret");

// --auth=user (default) | --auth=device | --auth=token | --auth=app | --auth=both
var authMode = (args.FirstOrDefault(a => a.StartsWith("--auth=", StringComparison.OrdinalIgnoreCase))
                  ?.Split('=', 2)[1] ?? "user").ToLowerInvariant();

if ((authMode == "app" || authMode == "both") && (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)))
{
    Console.Error.WriteLine($"--auth={authMode} requires PowerPlatform:ClientId / ClientSecret.");
    return 2;
}
if (string.IsNullOrWhiteSpace(tenantId))
{
    Console.Error.WriteLine("Missing PowerPlatform:TenantId.");
    return 2;
}

const string PpacBaseUrl = "https://api.powerplatform.com";
const string PpacScope   = "https://api.powerplatform.com/.default";

Console.WriteLine($"Tenant : {tenantId}");
Console.WriteLine($"Auth   : {authMode}");
Console.WriteLine($"BaseUrl: {PpacBaseUrl}");
Console.WriteLine($"Scope  : {PpacScope}");
Console.WriteLine();

IAccessTokenProvider tokenProvider = authMode switch
{
    "app"    => new AppOnlyAccessTokenProvider(tenantId, clientId, clientSecret, PpacScope),
    "device" => new DeviceCodeAccessTokenProvider(tenantId, PpacScope),
    "token"  => BuildStaticTokenProvider(),
    "both"   => new DeviceCodeAccessTokenProvider(tenantId, PpacScope), // primary = user; SP is the fallback
    _        => new InteractiveAccessTokenProvider(tenantId, PpacScope),
};

// Build a BAP fallback (legacy api.bap.microsoft.com / api.powerapps.com / api.flow.microsoft.com)
// for endpoints the new api.powerplatform.com surface still doesn't implement reliably.
IPublicClientApplication? userPcaForBap = (tokenProvider as DeviceCodeAccessTokenProvider)?.PublicApp
                                       ?? (tokenProvider as InteractiveAccessTokenProvider)?.PublicApp;
IConfidentialClientApplication? appCcaForBap = null;
if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
{
    appCcaForBap = ConfidentialClientApplicationBuilder.Create(clientId)
        .WithClientSecret(clientSecret)
        .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
        .Build();
}
var bap = (userPcaForBap != null || appCcaForBap != null)
    ? new BapFallback(userPcaForBap, appCcaForBap)
    : null;

// --bap-smoke : exercise just the BAP layer with a few known nav paths and exit.
if (args.Any(a => a.Equals("--bap-smoke", StringComparison.OrdinalIgnoreCase)))
{
    if (bap is null) { Console.WriteLine("No BAP fallback (no auth)."); return 0; }
    (string desc, (string Name, string? Key)[] nav)[] cases =
    {
        ("Environments list",  new[] { ("Environmentmanagement", (string?)null), ("Environments", (string?)null) }),
        ("EnvironmentGroups",  new[] { ("Environmentmanagement", (string?)null), ("EnvironmentGroups", (string?)null) }),
        ("DLP policies",       new[] { ("Governance", (string?)null), ("RuleBasedPolicies", (string?)null) }),
        ("BillingPolicies",    new[] { ("Licensing", (string?)null), ("BillingPolicies", (string?)null) }),
        ("AllowedThirdParty",  new[] { ("Appmanagement", (string?)null), ("AllowedThirdPartyApps", (string?)null) }),
    };
    foreach (var (desc, nav) in cases)
    {
        var (ok2, summary) = await bap.TryAsync(nav);
        Console.WriteLine($"  {desc,-22} {(ok2 ? "OK" : "FAIL")}  {summary}");
    }
    return 0;
}

static IAccessTokenProvider BuildStaticTokenProvider()
{
    var t = Environment.GetEnvironmentVariable("BEARER_TOKEN");
    if (string.IsNullOrWhiteSpace(t))
    {
        var path = Environment.GetEnvironmentVariable("BEARER_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            t = File.ReadAllText(path).Trim();
    }
    if (string.IsNullOrWhiteSpace(t))
        throw new InvalidOperationException("--auth=token requires BEARER_TOKEN env var or BEARER_TOKEN_FILE pointing to a file.");
    return new StaticBearerTokenProvider(t);
}
var authProvider  = new BaseBearerTokenAuthenticationProvider(tokenProvider);
var handlers      = KiotaClientFactory.CreateDefaultHandlers();
handlers.Insert(0, new ApiVersionHandler("2022-03-01-preview"));
var httpClient    = KiotaClientFactory.Create(handlers);
var adapter       = new HttpClientRequestAdapter(authProvider, httpClient: httpClient) { BaseUrl = PpacBaseUrl };
var sc            = new ServiceClient(adapter);

// Build the secondary (fallback) ServiceClient — the *other* auth — when --auth=both.
ServiceClient? scFallback = null;
string fallbackLabel = "";
if (authMode == "both")
{
    var fbToken    = new AppOnlyAccessTokenProvider(tenantId, clientId, clientSecret, PpacScope);
    var fbAuth     = new BaseBearerTokenAuthenticationProvider(fbToken);
    var fbHandlers = KiotaClientFactory.CreateDefaultHandlers();
    fbHandlers.Insert(0, new ApiVersionHandler("2022-03-01-preview"));
    var fbHttp     = KiotaClientFactory.Create(fbHandlers);
    var fbAdapter  = new HttpClientRequestAdapter(fbAuth, httpClient: fbHttp) { BaseUrl = PpacBaseUrl };
    scFallback     = new ServiceClient(fbAdapter);
    fallbackLabel  = " → fallback to app-only";
}

Console.WriteLine($"Effective : {(authMode == "both" ? "user (device-code)" : authMode)}{fallbackLabel}");
Console.WriteLine();

// ============================================================================
// Reflection walker — invokes every reachable GetAsync on every RequestBuilder.
// For item-indexed builders (e.g. Environments[envId]), uses ids harvested from
// previous list responses (env ids, group ids, policy ids, etc).
// ============================================================================
int ok = 0, fail = 0, fallbackOk = 0, bapOk = 0, attempted = 0;
var visitedPaths = new HashSet<string>(StringComparer.Ordinal);

// Per-key-name pool of sample ids we've harvested from previous responses.
// Key is the index parameter name (e.g. "environment", "group", "policyId"...).
var idPool = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

void HarvestIds(object? response)
{
    if (response is null) return;
    var t = response.GetType();
    var values = t.GetProperty("Value")?.GetValue(response);
    IEnumerable<object>? items = values switch
    {
        IEnumerable<object> oe => oe,
        IEnumerable e          => e.Cast<object>(),
        _                      => null
    };
    if (items is null) return;

    foreach (var item in items.Take(3)) // first 3 from each list is plenty
    {
        if (item is null) continue;
        var it = item.GetType();
        // Common id-like property names used across the SDK
        foreach (var p in it.GetProperties())
        {
            if (p.PropertyType != typeof(string)) continue;
            var name = p.Name.ToLowerInvariant();
            if (name is not ("name" or "id" or "environmentid" or "groupid" or "policyid"
                or "billingpolicyid" or "operationid" or "websiteid" or "appname" or "uniquename"))
                continue;
            var v = p.GetValue(item) as string;
            if (string.IsNullOrWhiteSpace(v)) continue;

            // Map SDK property names -> typical index parameter names
            var poolKeys = name switch
            {
                "name"            => new[] { "environment", "id", "name" },
                "id"              => new[] { "id", "environment", "group", "policy", "billingPolicy" },
                "environmentid"   => new[] { "environment", "id" },
                "groupid"         => new[] { "group" },
                "policyid"        => new[] { "policy" },
                "billingpolicyid" => new[] { "billingPolicy" },
                "operationid"     => new[] { "operation" },
                "websiteid"       => new[] { "website" },
                "appname"         => new[] { "appName", "uniqueName" },
                "uniquename"      => new[] { "uniqueName" },
                _ => Array.Empty<string>()
            };
            foreach (var k in poolKeys)
            {
                if (!idPool.TryGetValue(k, out var list)) idPool[k] = list = new List<string>();
                if (!list.Contains(v) && list.Count < 3) list.Add(v);
            }
        }
    }
}

string? PickId(string indexParamName)
{
    if (idPool.TryGetValue(indexParamName, out var list) && list.Count > 0)
        return list[0];
    return idPool.Values.SelectMany(l => l).FirstOrDefault();
}

IEnumerable<string> PickIds(string indexParamName, int max)
{
    if (idPool.TryGetValue(indexParamName, out var list) && list.Count > 0)
        return list.Take(max);
    // Fallback: take up to `max` distinct ids from any pool
    return idPool.Values.SelectMany(l => l).Distinct().Take(max);
}

async Task<object?> InvokeGetAsync(object builder, MethodInfo getAsync)
{
    var pars = getAsync.GetParameters();
    var args = pars.Select(p => p.HasDefaultValue ? p.DefaultValue : (object?)null).ToArray();
    var task = (Task)getAsync.Invoke(builder, args)!;
    await task.ConfigureAwait(false);
    return task.GetType().GetProperty("Result")?.GetValue(task);
}

string Summarise(object? response)
{
    if (response is null) return "(null)";
    var t = response.GetType();
    var v = t.GetProperty("Value")?.GetValue(response);
    if (v is ICollection col) return $"{col.Count} items";
    if (v is IEnumerable e)
    {
        int n = 0; foreach (var _ in e) n++;
        return $"{n} items";
    }
    return t.Name;
}

// Navigation step recorded as a tuple to avoid mid-file type declarations.
// Item1 = property name, Item2 = indexer key (null = property navigation).
object? Navigate(object root, IReadOnlyList<(string Name, string? Key)> steps)
{
    object? current = root;
    foreach (var step in steps)
    {
        if (current is null) return null;
        var t = current.GetType();
        if (step.Key is null)
        {
            var p = t.GetProperty(step.Name, BindingFlags.Public | BindingFlags.Instance);
            if (p is null || p.GetIndexParameters().Length != 0) return null;
            current = p.GetValue(current);
        }
        else
        {
            var idx = t.GetProperty(step.Name, BindingFlags.Public | BindingFlags.Instance);
            if (idx is null || idx.GetIndexParameters().Length != 1) return null;
            current = idx.GetValue(current, new object[] { step.Key });
        }
    }
    return current;
}

async Task TryGetAsync(object builder, string path, IReadOnlyList<(string Name, string? Key)> nav)
{
    var t = builder.GetType();
    var getAsync = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m => m.Name == "GetAsync"
                          && m.GetParameters().All(p => p.HasDefaultValue || !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null));
    if (getAsync is null) return;

    attempted++;
    Console.Write($"  [{attempted,3}] GET {path,-100} ");
    try
    {
        var resp = await InvokeGetAsync(builder, getAsync);
        Console.WriteLine($"OK   {Summarise(resp)}");
        ok++;
        HarvestIds(resp);
    }
    catch (TargetInvocationException tie) when (tie.InnerException is not null)
    {
        var msg = tie.InnerException.Message;
        if (msg.Length > 110) msg = msg[..110] + "...";
        var primaryFailed = $"FAIL {tie.InnerException.GetType().Name}: {msg}";
        if (await TryFallbackAsync(nav)) { fail++; return; }
        if (await TryBapAsync(nav)) { fail++; return; }
        Console.WriteLine(primaryFailed);
        fail++;
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 110) msg = msg[..110] + "...";
        var primaryFailed = $"FAIL {ex.GetType().Name}: {msg}";
        if (await TryFallbackAsync(nav)) { fail++; return; }
        if (await TryBapAsync(nav)) { fail++; return; }
        Console.WriteLine(primaryFailed);
        fail++;
    }
}

async Task<bool> TryFallbackAsync(IReadOnlyList<(string Name, string? Key)> nav)
{
    if (scFallback is null) return false;
    try
    {
        object? fbBuilder = Navigate(scFallback, nav);
        if (fbBuilder is null) return false;
        var fbType = fbBuilder.GetType();
        var fbGet = fbType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetAsync"
                              && m.GetParameters().All(p => p.HasDefaultValue || !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null));
        if (fbGet is null) return false;
        var resp = await InvokeGetAsync(fbBuilder, fbGet);
        Console.WriteLine($"OK*  {Summarise(resp)}   (via app-only fallback)");
        fallbackOk++;
        HarvestIds(resp);
        return true;
    }
    catch
    {
        return false;
    }
}

async Task<bool> TryBapAsync(IReadOnlyList<(string Name, string? Key)> nav)
{
    if (bap is null) return false;
    try
    {
        var (ok2, summary) = await bap.TryAsync(nav);
        if (ok2)
        {
            Console.WriteLine($"OK** {summary}   (via BAP legacy api)");
            bapOk++;
            return true;
        }
        // Surface BAP failures so we can see why the layer isn't helping.
        if (!string.IsNullOrEmpty(summary) && summary != "no BAP mapping")
            Console.WriteLine($"     ([BAP] {summary})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"     ([BAP] threw {ex.GetType().Name}: {ex.Message})");
    }
    return false;
}

async Task WalkAsync(object node, string path, int depth, int maxDepth, IReadOnlyList<(string Name, string? Key)> nav)
{
    if (node is null || depth > maxDepth) return;
    if (!visitedPaths.Add(path)) return;
    var t = node.GetType();

    await TryGetAsync(node, path, nav);

    // Recurse into nested non-indexed RequestBuilder properties
    foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead
                 && p.GetIndexParameters().Length == 0
                 && p.PropertyType.Name.EndsWith("RequestBuilder", StringComparison.Ordinal)
                 && p.PropertyType.Namespace?.StartsWith("Microsoft.PowerPlatform.Management") == true))
    {
        object? child = null;
        try { child = prop.GetValue(node); } catch { }
        if (child != null)
        {
            var nextNav = nav.Concat(new (string, string?)[] { (prop.Name, null) }).ToList();
            await WalkAsync(child, $"{path}.{prop.Name}", depth + 1, maxDepth, nextNav);
        }
    }

    // Recurse into indexers — e.g. Environments[envId]. Iterate up to 3 ids per indexer.
    foreach (var idx in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 1
                 && p.GetIndexParameters()[0].ParameterType == typeof(string)
                 && p.PropertyType.Name.EndsWith("RequestBuilder", StringComparison.Ordinal)
                 && p.PropertyType.Namespace?.StartsWith("Microsoft.PowerPlatform.Management") == true))
    {
        var paramName = idx.GetIndexParameters()[0].Name ?? "id";
        var keys = PickIds(paramName, max: 3);
        foreach (var key in keys)
        {
            object? child;
            try { child = idx.GetValue(node, new object[] { key }); } catch { continue; }
            if (child is null) continue;
            var keyShort = key.Length > 8 ? key[..8] + "…" : key;
            var nextNav = nav.Concat(new (string, string?)[] { (idx.Name, key) }).ToList();
            await WalkAsync(child, $"{path}[{keyShort}]", depth + 1, maxDepth, nextNav);
        }
    }
}

// --------------------------------------------------------------------------
// Pass 1: collect ids by walking shallowly first (envs, groups, policies)
// --------------------------------------------------------------------------
Console.WriteLine("=== PASS 1: list endpoints (harvest ids) ===");
Console.WriteLine();
await WalkAsync(sc, "ServiceClient", 0, maxDepth: 3, nav: Array.Empty<(string, string?)>());

Console.WriteLine();
Console.WriteLine("Harvested id pool:");
foreach (var kv in idPool.OrderBy(k => k.Key))
    Console.WriteLine($"  {kv.Key,-20} -> {string.Join(", ", kv.Value.Select(v => v.Length > 8 ? v[..8] + "…" : v))}");
Console.WriteLine();

// --------------------------------------------------------------------------
// Pass 2: walk much deeper, now that we have ids to feed indexers
// --------------------------------------------------------------------------
visitedPaths.Clear();
Console.WriteLine("=== PASS 2: deep walk including item-indexed builders ===");
Console.WriteLine();
await WalkAsync(sc, "ServiceClient", 0, maxDepth: 12, nav: Array.Empty<(string, string?)>());

Console.WriteLine();
Console.WriteLine($"=========== Summary: attempted={attempted}  OK(primary)={ok}  OK(app-fallback)={fallbackOk}  OK(BAP)={bapOk}  FAIL={fail - fallbackOk - bapOk} ===========");
return 0;
