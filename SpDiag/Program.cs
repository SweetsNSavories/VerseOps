using System.Net.Http.Headers;
using System.Text;
using Microsoft.Identity.Client;

// Quick SP diagnostic — probes BAP + PPAC + per-env Dataverse with the App-only credentials.
// Usage:  dotnet run --project SpDiag -- <tenant> <clientId> <clientSecret> [seacassEnvId]

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: SpDiag <tenant> <clientId> <clientSecret> [envId]");
    return 2;
}
var tenant = args[0];
var clientId = args[1];
var secret = args[2];
var envId = args.Length > 3 ? args[3] : "bca72238-f578-ec0d-9b7d-e7c90e4bce18"; // SeaCass

var cca = ConfidentialClientApplicationBuilder.Create(clientId)
    .WithClientSecret(secret)
    .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenant}"))
    .Build();

async Task<(string? token, string? err)> Tok(string scope)
{
    try { var r = await cca.AcquireTokenForClient(new[] { scope }).ExecuteAsync(); return (r.AccessToken, null); }
    catch (Exception ex) { return (null, ex.Message); }
}

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

async Task Probe(string label, string method, string url, string scope, string? body = null)
{
    var (tok, terr) = await Tok(scope);
    if (tok is null) { Console.WriteLine($"[{label,-32}] TOKEN FAIL  {terr}"); return; }
    using var req = new HttpRequestMessage(new HttpMethod(method), url);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
    if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var resp = await http.SendAsync(req);
    sw.Stop();
    var b = await resp.Content.ReadAsStringAsync();
    if (b.Length > 220) b = b[..220].Replace('\n', ' ').Replace('\r', ' ') + "...";
    Console.WriteLine($"[{label,-32}] {(int)resp.StatusCode,3} {sw.ElapsedMilliseconds,5}ms {b}");
}

Console.WriteLine($"Tenant : {tenant}");
Console.WriteLine($"AppId  : {clientId}");
Console.WriteLine($"Env    : {envId}");
Console.WriteLine(new string('-', 100));

// Token acquisition smoke
foreach (var s in new[] {
    "https://api.bap.microsoft.com/.default",
    "https://service.powerapps.com/.default",
    "https://api.powerplatform.com/.default",
    "https://service.flow.microsoft.com/.default",
})
{
    var (tok, err) = await Tok(s);
    Console.WriteLine($"[token {s,-50}] {(tok != null ? "OK" : "FAIL " + err)}");
}
Console.WriteLine(new string('-', 100));

// BAP reads
await Probe("BAP env list (user scope)",  "GET", "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments?api-version=2021-04-01", "https://service.powerapps.com/.default");
await Probe("BAP env list (admin scope)", "GET", "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2021-04-01", "https://service.powerapps.com/.default");
await Probe("BAP DLP policies",           "GET", "https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01", "https://service.powerapps.com/.default");
await Probe("BAP tenant settings",        "POST", "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/listTenantSettings?api-version=2020-10-01", "https://service.powerapps.com/.default", "{}");
await Probe("BAP adminApplications list", "GET", "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/adminApplications?api-version=2020-10-01", "https://api.bap.microsoft.com/.default");

// PPAC reads
await Probe("PPAC env list",              "GET", "https://api.powerplatform.com/environmentmanagement/environments?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");
await Probe("PPAC env get (SeaCass)",     "GET", $"https://api.powerplatform.com/environmentmanagement/environments/{envId}?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");
await Probe("PPAC tenantCapacity",        "GET", "https://api.powerplatform.com/licensing/tenantCapacity?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");
await Probe("PPAC ruleBasedPolicies",     "GET", "https://api.powerplatform.com/governance/ruleBasedPolicies?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");
await Probe("PPAC env groups",            "GET", "https://api.powerplatform.com/environmentmanagement/environmentGroups?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");

// Per-env (these need Application User in SeaCass — already System Administrator per the screenshot)
await Probe("PPAC websites (SeaCass)",       "GET", $"https://api.powerplatform.com/powerpages/environments/{envId}/websites?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");
await Probe("PPAC appPackages (SeaCass)",    "GET", $"https://api.powerplatform.com/appmanagement/environments/{envId}/applicationPackages?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");
await Probe("PPAC connectors (SeaCass)",     "GET", $"https://api.powerplatform.com/connectivity/environments/{envId}/connectors?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default");

// Create env probes (just look at status — both will burn quota if 200 so use a marker name)
var createBody = "{ \"location\": \"unitedstates\", \"properties\": { \"displayName\": \"VerseOps Diag DELETE ME\", \"environmentSku\": \"Sandbox\", \"databaseType\": \"CommonDataService\", \"linkedEnvironmentMetadata\": { \"baseLanguage\": 1033, \"currency\": { \"code\": \"USD\" }, \"templates\": [] } } }";
await Probe("BAP create env (user scope)",   "POST", "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments?api-version=2021-04-01&retainOnProvisionFailure=false", "https://service.powerapps.com/.default", createBody);
await Probe("BAP create env (admin scope)",  "POST", "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2021-04-01&retainOnProvisionFailure=false", "https://service.powerapps.com/.default", createBody);
await Probe("PPAC create env",               "POST", "https://api.powerplatform.com/environmentmanagement/environments?api-version=2022-03-01-preview", "https://api.powerplatform.com/.default", createBody);

return 0;
