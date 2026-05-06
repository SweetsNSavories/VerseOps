using Microsoft.Extensions.Configuration;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.PowerPlatform.Management;
using Microsoft.PowerPlatform.Management.Models;
using VerseOps.SdkProbe;

// =============================================================================
// VerseOps.SdkProbe â€” systematic sweep of Microsoft.PowerPlatform.Management.
//
// Defaults:
//   - Public client id = Power Platform CLI ("9cee029c-6210-4654-90bb-17e6e9d36617")
//     which has every PPAC delegated permission a tenant user can consent to.
//   - Multi-audience MSAL: one sign-in, scope chosen per request host.
//   - Pre-resolves SeaCass env id (or whatever --env=... matches) so per-item
//     calls are deterministic against an env you actually own.
//   - userId from MSAL account, tenantId from CLI/env: injected into request
//     query parameters when the route exposes them.
//
// Usage:
//   VerseOps.SdkProbe.exe --auth=user [--env=SeaCass] [--client=<guid>]
//   VerseOps.SdkProbe.exe --auth=app  [requires PowerPlatform__ClientId/Secret]
//   VerseOps.SdkProbe.exe --auth=token [BEARER_TOKEN env var]
// =============================================================================

string Get(IConfiguration cfg, string key) => cfg[key]
    ?? Environment.GetEnvironmentVariable(key.Replace(":", "__"))
    ?? string.Empty;

var cfg = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var tenantId     = Get(cfg, "PowerPlatform:TenantId");
var clientId     = Get(cfg, "PowerPlatform:ClientId");
var clientSecret = Get(cfg, "PowerPlatform:ClientSecret");
var authMode     = (args.FirstOrDefault(a => a.StartsWith("--auth=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1] ?? "user").ToLowerInvariant();
var envFilter    = args.FirstOrDefault(a => a.StartsWith("--env=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1] ?? "SeaCass";
var publicCid    = args.FirstOrDefault(a => a.StartsWith("--client=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
                   ?? "9cee029c-6210-4654-90bb-17e6e9d36617"; // Power Platform CLI
var inspectQps   = args.Any(a => a.Equals("--inspect-qps", StringComparison.OrdinalIgnoreCase));
var catalogCrud  = args.Any(a => a.Equals("--catalog-crud", StringComparison.OrdinalIgnoreCase));
var runCrud      = args.Any(a => a.Equals("--crud", StringComparison.OrdinalIgnoreCase));
var sandboxEnv   = args.FirstOrDefault(a => a.StartsWith("--sandbox-env=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1] ?? "nyctest";

// --inspect-qps: enumerate every *RequestBuilder.*GetQueryParameters nested type in the SDK
// and dump (BuilderName, QpProperty, Type) tuples to sdk-qp-shapes.txt. No network needed.
// Used to discover the actual SDK QP property names so BuildRequestConfigAction can target
// them by name instead of guessing.
if (inspectQps)
{
    var sdkAsm = typeof(ServiceClient).Assembly;
    var rows = new List<string>();
    foreach (var t in sdkAsm.GetTypes()
                            .Where(t => t.Name.EndsWith("RequestBuilder", StringComparison.Ordinal))
                            .OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        foreach (var nested in t.GetNestedTypes(System.Reflection.BindingFlags.Public)
                                .Where(n => n.Name.EndsWith("GetQueryParameters", StringComparison.Ordinal)))
        {
            foreach (var p in nested.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                    .Where(p => p.CanRead && p.CanWrite))
            {
                rows.Add($"{t.FullName}.{nested.Name}.{p.Name}\t{p.PropertyType.FullName}");
            }
        }
    }
    var outFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sdk-qp-shapes.txt"));
    File.WriteAllLines(outFile, rows);
    Console.WriteLine($"Wrote {rows.Count} QP entries to {outFile}");
    return 0;
}

// --catalog-crud: enumerate every Post/Put/Patch/Delete-Async method on every *RequestBuilder
// in the SDK and dump (BuilderFullName, Verb, BodyType, ReturnType, QPs) to sdk-crud-catalog.txt.
// No network. Used to scope the CRUD test surface before any mutating call is made.
if (catalogCrud)
{
    var sdkAsm = typeof(ServiceClient).Assembly;
    var rows = new List<string> { "Verb\tBuilderFullName\tMethod\tBodyType\tReturnType\tQpProperties" };
    var verbs = new[] { "PostAsync", "PutAsync", "PatchAsync", "DeleteAsync" };
    int methodCount = 0, builderCount = 0;
    var seenBuilders = new HashSet<string>(StringComparer.Ordinal);
    foreach (var t in sdkAsm.GetTypes()
                            .Where(t => t.Name.EndsWith("RequestBuilder", StringComparison.Ordinal)
                                     && t.Namespace?.StartsWith("Microsoft.PowerPlatform.Management", StringComparison.Ordinal) == true)
                            .OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        var methods = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                       .Where(m => verbs.Contains(m.Name, StringComparer.Ordinal))
                       .OrderBy(m => m.Name, StringComparer.Ordinal)
                       .ToArray();
        if (methods.Length == 0) continue;
        if (seenBuilders.Add(t.FullName ?? t.Name)) builderCount++;

        // Find this builder's QPs (any nested type ending in QueryParameters, includes Get/Post/etc.)
        var qpNames = t.GetNestedTypes(System.Reflection.BindingFlags.Public)
                       .Where(n => n.Name.EndsWith("QueryParameters", StringComparison.Ordinal)
                                && !n.Name.EndsWith("GetQueryParameters", StringComparison.Ordinal))
                       .SelectMany(n => n.GetProperties().Where(p => p.CanRead && p.CanWrite).Select(p => p.Name))
                       .Distinct(StringComparer.Ordinal)
                       .ToArray();
        var qpStr = qpNames.Length == 0 ? "-" : string.Join(",", qpNames);

        foreach (var m in methods)
        {
            methodCount++;
            // Skip the cancellation-token / config-action params; the body is the first
            // parameter that is neither CancellationToken nor a generic Action<>.
            var bodyParam = m.GetParameters()
                .FirstOrDefault(p => p.ParameterType != typeof(CancellationToken)
                                  && !(p.ParameterType.IsGenericType
                                    && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>)));
            var bodyType = bodyParam?.ParameterType.FullName ?? "-";
            var ret = m.ReturnType.IsGenericType
                ? $"{m.ReturnType.Name}<{string.Join(",", m.ReturnType.GetGenericArguments().Select(g => g.Name))}>"
                : m.ReturnType.Name;
            rows.Add($"{m.Name}\t{t.FullName}\t{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})\t{bodyType}\t{ret}\t{qpStr}");
        }
    }
    var outFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sdk-crud-catalog.txt"));
    File.WriteAllLines(outFile, rows);
    Console.WriteLine($"Wrote {methodCount} CRUD methods across {builderCount} builders to {outFile}");
    // Tally per verb
    var byVerb = rows.Skip(1).GroupBy(r => r.Split('\t')[0]).OrderBy(g => g.Key);
    foreach (var g in byVerb) Console.WriteLine($"  {g.Key,-12} {g.Count()}");
    return 0;
}

if (string.IsNullOrWhiteSpace(tenantId))
{
    Console.Error.WriteLine("ERROR: Missing PowerPlatform:TenantId. Set via env var PowerPlatform__TenantId.");
    return 2;
}
if (authMode == "app" && (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)))
{
    Console.Error.WriteLine("ERROR: --auth=app requires PowerPlatform:ClientId and PowerPlatform:ClientSecret.");
    return 2;
}

const string PpacBaseUrl = "https://api.powerplatform.com";
const string PpacScope   = "https://api.powerplatform.com/.default";
const string ApiVersion  = "2022-03-01-preview";

Console.WriteLine($"Tenant      : {tenantId}");
Console.WriteLine($"Auth        : {authMode}");
Console.WriteLine($"PublicClient: {publicCid}");
Console.WriteLine($"BaseUrl     : {PpacBaseUrl}");
Console.WriteLine($"Env filter  : {envFilter}");

// Build the auth provider. For --auth=user, we use the host-aware multi-audience provider.
IAccessTokenProvider tokenProvider;
HostAwareTokenProvider? hostAware = null;
switch (authMode)
{
    case "app":
        tokenProvider = new AppOnlyTokenProvider(tenantId, clientId, clientSecret, PpacScope);
        break;
    case "token":
        var bt = Environment.GetEnvironmentVariable("BEARER_TOKEN")
                 ?? throw new InvalidOperationException("--auth=token requires BEARER_TOKEN env var");
        tokenProvider = new StaticBearerTokenProvider(bt);
        break;
    default:
        hostAware = new HostAwareTokenProvider(tenantId, publicCid);
        tokenProvider = hostAware;
        break;
}

var authProv = new BaseBearerTokenAuthenticationProvider(tokenProvider);
var handlers = KiotaClientFactory.CreateDefaultHandlers();
handlers.Insert(0, new Microsoft.PowerPlatform.Management.ApiVersionHandler(ApiVersion));
handlers.Insert(0, new ErrorBodyCaptureHandler()); // outermost so it sees the final response
var http     = KiotaClientFactory.Create(handlers);
http.Timeout = TimeSpan.FromSeconds(150); // global ceiling; per-call shorter timeout via CancellationToken in SweepEngine
var adapter  = new HttpClientRequestAdapter(authProv, httpClient: http) { BaseUrl = PpacBaseUrl };
var sc       = new ServiceClient(adapter);

// First call forces sign-in (device code). Use a cheap call to also resolve user identity.
Console.WriteLine();
Console.WriteLine("Resolving user identity & SeaCass env...");
string? userId = null, upn = null;
string? pinnedEnvId = null;
try
{
    // Trigger token acquisition + capture identity from MSAL cache.
    var envList = await sc.Environmentmanagement.Environments.GetAsync();
    if (hostAware != null)
    {
        var (oid, account, tid) = await hostAware.GetUserIdentityAsync();
        userId = oid;
        upn = account;
        if (!string.IsNullOrEmpty(tid)) tenantId = tid;
    }
    Console.WriteLine($"  user        : {upn ?? "(unknown)"}  oid={userId ?? "(none)"}");

    var match = envList?.Value?
        .Where(e => !string.IsNullOrEmpty(e.DisplayName))
        .FirstOrDefault(e => string.Equals(e.DisplayName, envFilter, StringComparison.OrdinalIgnoreCase))
        ?? envList?.Value?
            .Where(e => !string.IsNullOrEmpty(e.DisplayName))
            .FirstOrDefault(e => e.DisplayName!.Contains(envFilter, StringComparison.OrdinalIgnoreCase));
    if (match != null)
    {
        pinnedEnvId = match.Id; // PPAC item routes use the EntraID-style guid in the Id field
        Console.WriteLine($"  pinned env  : {match.DisplayName}  id={pinnedEnvId}  url={match.Url}");
    }
    else
    {
        Console.WriteLine($"  pinned env  : (no env matched filter '{envFilter}', falling back to first list item)");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  WARN: identity/env resolve failed: {ex.GetType().Name}: {ex.Message}");
}

var outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "probe-results.json"));
var engine = new SweepEngine(sc, outPath, userId, tenantId, pinnedEnvId);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await engine.RunAsync(cts.Token);

    // Optional CRUD pass — only when explicitly requested. Resolves the sandbox env separately
    // so we never accidentally mutate the GET-pass pinned env.
    if (runCrud)
    {
        Console.WriteLine();
        Console.WriteLine("=== Resolving CRUD sandbox env ===");
        string? sandboxEnvId = null, sandboxEnvName = null;
        try
        {
            var envList = await sc.Environmentmanagement.Environments.GetAsync(cancellationToken: cts.Token);
            var match = envList?.Value?
                .Where(e => !string.IsNullOrEmpty(e.DisplayName))
                .FirstOrDefault(e => string.Equals(e.DisplayName, sandboxEnv, StringComparison.OrdinalIgnoreCase));
            if (match != null) { sandboxEnvId = match.Id; sandboxEnvName = match.DisplayName; }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  WARN: sandbox env resolve failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (string.IsNullOrEmpty(sandboxEnvId))
        {
            Console.WriteLine($"  ERROR: sandbox env '{sandboxEnv}' not found. Pass --sandbox-env=<DisplayName>.");
            return 3;
        }
        Console.WriteLine($"  sandbox env : {sandboxEnvName}  id={sandboxEnvId}");
        var crudOut = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "crud-results.json"));
        var crudEngine = new CrudPassEngine(sc, crudOut, userId, tenantId, sandboxEnvId, sandboxEnvName!);
        await crudEngine.RunAsync(cts.Token);
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 130;
}
