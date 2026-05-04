using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace VerseOps.SdkRunner;

/// <summary>
/// Tertiary fallback to the legacy Business Application Platform (BAP) REST APIs.
///
/// Routes/api-versions are taken directly from the
/// <c>Microsoft.PowerApps.Administration.PowerShell</c> module (v2.0.217), which is
/// the de-facto authoritative reference for BAP — Microsoft maintains it on the
/// PowerShell Gallery. Each $route literal in that module's Get-* cmdlets gave us
/// one row in the mapping table below.
///
/// Hosts:
///   api.bap.microsoft.com         BAP        (environments, governance, billing)
///   api.powerapps.com             PowerApps  (apps, connectors, connections)
///   api.flow.microsoft.com        Flow       (Power Automate flows)
/// </summary>
public sealed class BapFallback
{
    private readonly IPublicClientApplication? _userPca;
    private readonly IConfidentialClientApplication? _appCca;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, string> _tokenCache = new();
    private readonly HashSet<string> _userScopeFailed = new(StringComparer.OrdinalIgnoreCase);

    private const string BapHost       = "api.bap.microsoft.com";
    private const string PowerAppsHost = "api.powerapps.com";
    private const string FlowHost      = "api.flow.microsoft.com";

    // Delegated tokens: the PowerApps audience covers BAP, PowerApps and Flow.
    private const string DelegatedScope = "https://service.powerapps.com/.default";
    // App-only tokens: the SERVICE audience (not the api.* hostname) is what BAP accepts.
    // The Microsoft.PowerApps.Administration.PowerShell module uses these audiences.
    private const string BapAppScope       = "https://service.powerapps.com/.default";
    private const string PowerAppsAppScope = "https://service.powerapps.com/.default";
    private const string FlowAppScope      = "https://service.flow.microsoft.com/.default";

    public BapFallback(IPublicClientApplication? userPca, IConfidentialClientApplication? appCca)
    {
        _userPca = userPca;
        _appCca  = appCca;
    }

    private async Task<string?> GetTokenAsync(string appOnlyScope)
    {
        var cacheKey = appOnlyScope;
        if (_tokenCache.TryGetValue(cacheKey, out var cached)) return cached;

        // Try delegated user first — one delegated token covers all 3 hosts.
        if (_userPca != null && !_userScopeFailed.Contains(DelegatedScope))
        {
            try
            {
                var accounts = await _userPca.GetAccountsAsync();
                if (accounts.Any())
                {
                    var r = await _userPca.AcquireTokenSilent(new[] { DelegatedScope }, accounts.First()).ExecuteAsync();
                    _tokenCache[cacheKey] = r.AccessToken;
                    return r.AccessToken;
                }
            }
            catch { _userScopeFailed.Add(DelegatedScope); }
        }

        // Fall back to app-only with the per-host audience
        if (_appCca != null)
        {
            try
            {
                var r = await _appCca.AcquireTokenForClient(new[] { appOnlyScope }).ExecuteAsync();
                _tokenCache[cacheKey] = r.AccessToken;
                return r.AccessToken;
            }
            catch { }
        }
        return null;
    }

    private async Task<(bool ok, string summary)> CallAsync(string url, string appOnlyScope)
    {
        var token = await GetTokenAsync(appOnlyScope);
        if (token is null) return (false, "no BAP token");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var snippet = body.Length > 70 ? body[..70].Replace("\r"," ").Replace("\n"," ") + "..." : body;
                return (false, $"BAP {(int)resp.StatusCode}: {snippet}");
            }
            return (true, SummariseJson(body));
        }
        catch (Exception ex)
        {
            return (false, $"BAP exc: {ex.GetType().Name}");
        }
    }

    private static string SummariseJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
                return $"{v.GetArrayLength()} items";
            if (root.ValueKind == JsonValueKind.Array)
                return $"{root.GetArrayLength()} items";
            return $"object ({body.Length}b)";
        }
        catch { return $"non-json ({body.Length}b)"; }
    }

    /// <summary>
    /// Maps the SDK navigation breadcrumb to a BAP route. (true, summary) on success.
    /// </summary>
    public async Task<(bool ok, string summary)> TryAsync(IReadOnlyList<(string Name, string? Key)> nav)
    {
        if (nav.Count == 0) return (false, "");
        var area = nav[0].Name;

        // ===== Environmentmanagement =====
        if (area == "Environmentmanagement")
        {
            // Get-AdminPowerAppEnvironment, api-version 2021-04-01
            if (nav.Count >= 2 && nav[1].Name == "Environments")
            {
                var envId = nav[1].Key;
                if (nav.Count == 2 && envId is null)
                    return await CallAsync($"https://{BapHost}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?$expand=permissions&api-version=2021-04-01", BapAppScope);
                if (nav.Count == 2 && envId != null)
                    return await CallAsync($"https://{BapHost}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{envId}?$expand=permissions&api-version=2021-04-01", BapAppScope);
            }
            if (nav.Count >= 2 && nav[1].Name == "EnvironmentGroups" && nav[1].Key is null)
                return (false, "BAP has no EnvironmentGroups (Managed Environments is new-platform only)");
            // lifecycleOperations endpoint (api-version 2019-05-01)
            if (nav.Count >= 2 && nav[1].Name == "Operations" && nav[1].Key != null)
                return await CallAsync($"https://{BapHost}/providers/Microsoft.BusinessAppPlatform/lifecycleOperations/{nav[1].Key}?api-version=2019-05-01", BapAppScope);
        }

        // ===== Powerapps =====
        if (area == "Powerapps")
        {
            // Cross-tenant apps list
            if (nav.Count == 2 && nav[1].Name == "Apps" && nav[1].Key is null)
                return await CallAsync($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/apps?api-version=2016-11-01", PowerAppsAppScope);

            if (nav.Count >= 3 && nav[1].Name == "Environments" && nav[1].Key != null)
            {
                var envId = nav[1].Key!;
                var sub = nav[2];
                // Apps list / single   (Get-AdminPowerApp, 2016-11-01)
                if (sub.Name == "Apps")
                {
                    if (sub.Key is null)
                        return await CallAsync($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/scopes/admin/environments/{envId}/apps?api-version=2016-11-01&$top=250&$expand=permissions", PowerAppsAppScope);
                    return await CallAsync($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/scopes/admin/environments/{envId}/apps/{sub.Key}?api-version=2016-11-01", PowerAppsAppScope);
                }
                // Connectors / APIs   (Get-AdminPowerAppConnector, 2017-05-01)
                if (sub.Name == "Connectors" || sub.Name == "Apis")
                    return await CallAsync($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/scopes/admin/environments/{envId}/apis?api-version=2017-05-01", PowerAppsAppScope);
                // Connections   (Get-AdminPowerAppConnection, 2016-11-01)
                if (sub.Name == "Connections")
                    return await CallAsync($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/scopes/admin/environments/{envId}/connections?api-version=2016-11-01", PowerAppsAppScope);
            }
        }

        // ===== Power Automate flows =====
        if ((area == "Powerautomate" || area == "Powervirtualagents")
            && nav.Count >= 3 && nav[1].Name == "Environments" && nav[1].Key != null && nav[2].Name == "Flows")
        {
            var envId = nav[1].Key!;
            return await CallAsync($"https://{FlowHost}/providers/Microsoft.ProcessSimple/scopes/admin/environments/{envId}/v2/flows?api-version=2016-11-01&$top=50", FlowAppScope);
        }

        // ===== Governance =====
        if (area == "Governance" && nav.Count >= 2 && nav[1].Name == "RuleBasedPolicies")
        {
            // Get-DlpPolicy, PowerPlatform.Governance/v1 (no api-version param)
            if (nav[1].Key is null)
                return await CallAsync($"https://{BapHost}/providers/PowerPlatform.Governance/v1/policies?$top=50", BapAppScope);
            return await CallAsync($"https://{BapHost}/providers/PowerPlatform.Governance/v1/policies/{nav[1].Key}", BapAppScope);
        }

        // ===== Licensing =====
        // BillingPolicies is a new-platform-only feature; BAP returns 404 for all api-versions.
        // Skip mapping entirely — the new SDK already serves it under user auth.

        // ===== Appmanagement =====
        if (area == "Appmanagement" && nav.Count == 2 && nav[1].Name == "AllowedThirdPartyApps")
            return await CallAsync($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/scopes/admin/allowedThirdPartyApps?api-version=2017-05-01", PowerAppsAppScope);

        // ===== Usermanagement =====
        if (area == "Usermanagement" && nav.Count >= 2 && nav[1].Name == "Users" && nav[1].Key != null)
            return await CallAsync($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/scopes/admin/users/{nav[1].Key}?api-version=2016-11-01", PowerAppsAppScope);

        return (false, "no BAP mapping");
    }
}
