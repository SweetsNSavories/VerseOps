using System.Net.Http.Headers;
using System.Text;
using Microsoft.Identity.Client;

// Comprehensive BAP sweep — runs every well-known BAP/PowerApps/Flow admin route
// against the supplied SP. No SDK exists for BAP; routes are taken verbatim from
// (a) the Microsoft.PowerApps.Administration.PowerShell module v2.0.217 source,
// (b) the InventoryPuller production code, and (c) the WPF catalog.
//
// usage:
//   SP    : dotnet run --project BapDiag -- <tenant> <clientId> <secret> [envIdsCsv]
//   device: dotnet run --project BapDiag -- --device <tenant> [envIdsCsv]

bool device = args.Length > 0 && args[0] == "--device";
string tenant; string[] envSeed;
Func<string, Task<string>> Tok;

if (device)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: BapDiag --device <tenant> [envIdsCsv]"); return 2; }
    tenant = args[1];
    envSeed = args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    // Microsoft Azure PowerShell well-known public client id (multi-tenant, has Power Platform consents)
    const string pscid = "1950a258-227b-4e31-a9cf-717495945fc2";
    var pca = PublicClientApplicationBuilder.Create(pscid)
        .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenant}"))
        .WithDefaultRedirectUri()
        .Build();
    Tok = async (scope) =>
    {
        try
        {
            var accounts = await pca.GetAccountsAsync();
            if (accounts.Any())
                return (await pca.AcquireTokenSilent(new[] { scope }, accounts.First()).ExecuteAsync()).AccessToken;
        }
        catch { }
        var r = await pca.AcquireTokenWithDeviceCode(new[] { scope }, dc =>
        {
            Console.WriteLine();
            Console.WriteLine("==> " + dc.Message);
            Console.WriteLine();
            return Task.CompletedTask;
        }).ExecuteAsync();
        return r.AccessToken;
    };
}
else
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: BapDiag <tenant> <clientId> <secret> [envIdsCsv]"); return 2; }
    tenant = args[0];
    var clientId = args[1];
    var secret   = args[2];
    envSeed = args.Length > 3 ? args[3].Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    var cca = ConfidentialClientApplicationBuilder.Create(clientId)
        .WithClientSecret(secret)
        .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenant}"))
        .Build();
    Tok = async (scope) => (await cca.AcquireTokenForClient(new[] { scope }).ExecuteAsync()).AccessToken;
}

// All BAP family audiences — admin scope on each host accepts these tokens.
var bapTok      = await Tok("https://service.powerapps.com/.default");
var bapHostTok  = await Tok("https://api.bap.microsoft.com/.default");
var paHostTok   = await Tok("https://service.powerapps.com/.default");
var flowHostTok = await Tok("https://service.flow.microsoft.com/.default");

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
int ok = 0, fail = 0; var byStatus = new Dictionary<int,int>();
async Task Probe(string area, string label, string method, string url, string token, string? body = null)
{
    using var req = new HttpRequestMessage(new HttpMethod(method), url);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var resp = await http.SendAsync(req);
        sw.Stop();
        var b = await resp.Content.ReadAsStringAsync();
        var snippet = b.Length > 100 ? b[..100].Replace('\n',' ').Replace('\r',' ') + "..." : b.Replace('\n',' ').Replace('\r',' ');
        var s = (int)resp.StatusCode;
        byStatus[s] = byStatus.GetValueOrDefault(s) + 1;
        if (resp.IsSuccessStatusCode) { ok++; Console.WriteLine($"  {s,3} {sw.ElapsedMilliseconds,5}ms  {area,-14} {label,-46} {snippet}"); }
        else { fail++; Console.WriteLine($"  {s,3} {sw.ElapsedMilliseconds,5}ms  {area,-14} {label,-46} {snippet}"); }
    }
    catch (Exception ex) { fail++; Console.WriteLine($"  EXC       {area,-14} {label,-46} {ex.GetType().Name}: {ex.Message}"); }
}

const string Bap = "https://api.bap.microsoft.com";
const string Pa  = "https://api.powerapps.com";
const string Fl  = "https://api.flow.microsoft.com";

Console.WriteLine($"Tenant : {tenant}\nAuth   : {(device ? "device-code (user)" : "client-credentials (SP)")}");
Console.WriteLine(new string('=', 130));

// ---- Tenant-level BAP routes -------------------------------------------------
Console.WriteLine("\n[Tenant scope]");
await Probe("Tenant",  "Locations (geos)",                 "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/locations?api-version=2021-04-01", bapTok);
await Probe("Tenant",  "List tenant settings",             "POST", $"{Bap}/providers/Microsoft.BusinessAppPlatform/listTenantSettings?api-version=2020-10-01", bapTok, "{}");
await Probe("Tenant",  "List tenant settings (admin)",     "POST", $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/listTenantSettings?api-version=2020-10-01", bapTok, "{}");
await Probe("Tenant",  "TenantInfo",                       "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/tenantInfo?api-version=2020-10-01", bapTok);
await Probe("Tenant",  "Capacity (admin tenant)",          "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/tenant/capacity?api-version=2021-04-01", bapTok);
await Probe("Tenant",  "Tenant capacities (admin)",        "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/capacities?api-version=2020-10-01", bapTok);
await Probe("Tenant",  "Tenant licenses (admin)",          "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/licensing/tenantLicenses?api-version=2020-10-01", bapTok);
await Probe("Tenant",  "AdminApplications list",           "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/adminApplications?api-version=2020-10-01", bapHostTok);

// ---- Environments (user + admin scope, both api versions) --------------------
Console.WriteLine("\n[Environments]");
await Probe("Env",     "List (user 2021-04-01)",           "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/environments?api-version=2021-04-01", bapTok);
await Probe("Env",     "List (admin 2021-04-01)",          "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2021-04-01", bapTok);
await Probe("Env",     "List (admin 2020-10-01 +cap)",     "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01&$expand=properties/capacity", bapTok);
await Probe("Env",     "List (admin 2016-11-01 +perm)",    "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2016-11-01&$expand=permissions,properties.capacity", bapTok);

// Pull a few env ids from the working list call to drive the per-env probes.
var envIds = new List<string>();
try
{
    using var req = new HttpRequestMessage(HttpMethod.Get,
        $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2021-04-01");
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bapTok);
    using var resp = await http.SendAsync(req);
    var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    if (json.RootElement.TryGetProperty("value", out var arr))
        foreach (var e in arr.EnumerateArray().Take(3))
            if (e.TryGetProperty("name", out var n)) envIds.Add(n.GetString()!);
}
catch { }
if (envSeed.Length > 0) envIds = envSeed.ToList();
if (envIds.Count == 0) envIds.Add("00000000-0000-0000-0000-000000000000"); // sentinel
Console.WriteLine($"\n  using env ids: {string.Join(", ", envIds.Select(i => i[..8] + "..."))}");

// ---- Per-environment routes --------------------------------------------------
Console.WriteLine("\n[Per-env Apps / Flows / Connections / Capacity]");
foreach (var env in envIds)
{
    await Probe("Env",         $"Get {env[..8]}.. (admin)",      "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{env}?api-version=2020-10-01&$expand=properties/capacity", bapTok);
    await Probe("Env",         $"Capacity {env[..8]}..",         "GET",  $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{env}/capacity?api-version=2021-04-01", bapTok);
    await Probe("Apps",        $"PowerApps {env[..8]}..",        "GET",  $"{Pa}/providers/Microsoft.PowerApps/scopes/admin/environments/{env}/apps?api-version=2020-06-01", paHostTok);
    await Probe("Apps",        $"PowerApps {env[..8]}.. (2021)", "GET",  $"{Pa}/providers/Microsoft.PowerApps/scopes/admin/environments/{env}/apps?api-version=2021-02-01", paHostTok);
    await Probe("Connections", $"Connections {env[..8]}..",      "GET",  $"{Pa}/providers/Microsoft.PowerApps/scopes/admin/environments/{env}/connections?api-version=2020-06-01", paHostTok);
    await Probe("Connectors",  $"Connectors {env[..8]}..",       "GET",  $"{Pa}/providers/Microsoft.PowerApps/scopes/admin/environments/{env}/apis?api-version=2020-06-01", paHostTok);
    await Probe("Flows",       $"Flows {env[..8]}.. (v2)",       "GET",  $"{Fl}/providers/Microsoft.ProcessSimple/scopes/admin/environments/{env}/v2/flows?api-version=2016-11-01", flowHostTok);
    await Probe("Flows",       $"Flows {env[..8]}.. (v1)",       "GET",  $"{Fl}/providers/Microsoft.ProcessSimple/scopes/admin/environments/{env}/flows?api-version=2016-11-01", flowHostTok);
}

// ---- Catalog / location lookups ----------------------------------------------
Console.WriteLine("\n[Catalog data]");
await Probe("Catalog", "Currencies (US)",   "GET", $"{Bap}/providers/Microsoft.BusinessAppPlatform/locations/unitedstates/environmentCurrencies?api-version=2021-04-01", bapTok);
await Probe("Catalog", "Languages (US)",    "GET", $"{Bap}/providers/Microsoft.BusinessAppPlatform/locations/unitedstates/environmentLanguages?api-version=2021-04-01", bapTok);
await Probe("Catalog", "Templates (US)",    "GET", $"{Bap}/providers/Microsoft.BusinessAppPlatform/locations/unitedstates/environmentTemplates?api-version=2021-04-01", bapTok);
await Probe("Catalog", "Skus (US)",         "GET", $"{Bap}/providers/Microsoft.BusinessAppPlatform/locations/unitedstates/environmentSkus?api-version=2021-04-01", bapTok);

// ---- DLP / Governance --------------------------------------------------------
Console.WriteLine("\n[DLP / Governance]");
await Probe("DLP",     "List policies (v2)",                 "GET", $"{Bap}/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01", bapTok);
await Probe("DLP",     "List policies (v1 admin)",           "GET", $"{Bap}/providers/PowerPlatform.Governance/v1/policies?api-version=2018-01-01", bapTok);
await Probe("DLP",     "Connectors classification",          "GET", $"{Bap}/providers/PowerPlatform.Governance/v2/connectorClassifications?api-version=2018-01-01", bapTok);

// ---- Admin reports / consumption ---------------------------------------------
Console.WriteLine("\n[Admin reports]");
await Probe("Reports", "Consumption (tenant)",               "GET", $"{Bap}/providers/Microsoft.BusinessAppPlatform/scopes/admin/consumption?api-version=2021-04-01", bapTok);
await Probe("Reports", "AdoptionDashboard",                  "GET", $"{Pa}/providers/Microsoft.PowerApps/scopes/admin/adoptionDashboard?api-version=2020-06-01", paHostTok);

Console.WriteLine(new string('=', 130));
Console.WriteLine($"Done. ok={ok}  fail={fail}");
Console.WriteLine("Status distribution: " + string.Join("  ", byStatus.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}")));
return 0;
