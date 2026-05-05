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
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 130;
}
