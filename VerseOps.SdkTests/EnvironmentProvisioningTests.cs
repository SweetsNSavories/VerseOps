using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Direct-HTTP coverage for the PPAC <c>environmentmanagement/provisioning/*</c>
/// surface (Power Platform API v2024-10-01) — the routes documented in the
/// Microsoft Learn REST reference but NOT yet exposed by the
/// Microsoft.PowerPlatform.Management 2.0.3317.207 NuGet SDK (the SDK still
/// pins api-version 2022-03-01-preview and has no Provisioning request builder).
///
/// Six endpoints covered:
///   GET   /provisioning/locations
///   GET   /provisioning/locations/{location}/currencies
///   GET   /provisioning/locations/{location}/languages
///   GET   /provisioning/locations/{location}/templates
///   PATCH /provisioning/environments/{environmentId}/link            (gated, opt-in)
///   POST  /provisioning/create                                       (gated, opt-in)
///
/// Read-only GETs run by default. The two mutating endpoints only fire when
/// <c>VERSEOPS_CREATE_SANDBOX=1</c> (provision) or <c>VERSEOPS_LINK_DV=1</c> (link)
/// are set in the environment — those provision a real sandbox / mutate a real org,
/// so we never invoke them from a routine green run.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentProvisioningTests
{
    private const string PpacBase   = "https://api.powerplatform.com";
    private const string ApiVersion = "2024-10-01";

    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentProvisioningTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    // ---------- READ-ONLY ----------

    [SkippableFact]
    public async Task GET_Locations_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var (status, body) = await SendAsync(HttpMethod.Get,
            $"{PpacBase}/environmentmanagement/provisioning/locations?api-version={ApiVersion}").ConfigureAwait(false);
        LogResult("GET /provisioning/locations", status, body);
        Assert.True((int)status < 400, $"HTTP {(int)status}");
        // Body shape: { "collection": [ {name,code,displayName,canProvisionDatabase,isDefault,isDisabled} ], "continuationToken": ... }
        Assert.Contains("collection", body, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> PerLocationData()
    {
        // Each row exercises one of the three per-location GETs against unitedstates,
        // which the docs flag as the default-availability geo. Adding more locations
        // here multiplies coverage but also multiplies test time on cold tenants.
        var locations = new[] { "unitedstates" };
        foreach (var loc in locations)
        {
            yield return new object[] { loc, "currencies" };
            yield return new object[] { loc, "languages"  };
            yield return new object[] { loc, "templates"  };
        }
    }

    [SkippableTheory]
    [MemberData(nameof(PerLocationData))]
    public async Task GET_PerLocation_Resource_Succeeds(string location, string resource)
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var (status, body) = await SendAsync(HttpMethod.Get,
            $"{PpacBase}/environmentmanagement/provisioning/locations/{location}/{resource}?api-version={ApiVersion}")
            .ConfigureAwait(false);
        LogResult($"GET /provisioning/locations/{location}/{resource}", status, body);
        Assert.True((int)status < 400, $"HTTP {(int)status}");
    }

    // ---------- MUTATING (opt-in only) ----------

    [SkippableFact]
    public async Task POST_Create_Provisions_Sandbox()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        // Hard gate. Set VERSEOPS_CREATE_SANDBOX=1 in the shell to actually fire this.
        // The request below provisions a real sandbox org under the signed-in tenant —
        // it costs nothing in $ but consumes one of the tenant's sandbox slots and
        // takes minutes to complete. Default test runs must never trigger it.
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_CREATE_SANDBOX"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_CREATE_SANDBOX=1 to actually provision a sandbox environment.");

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var location    = Environment.GetEnvironmentVariable("VERSEOPS_LOCATION") ?? "unitedstates";
        var displayName = Environment.GetEnvironmentVariable("VERSEOPS_DISPLAY_NAME") ?? $"verseops-sandbox-{stamp}";
        var currency    = Environment.GetEnvironmentVariable("VERSEOPS_CURRENCY") ?? "USD";
        var language    = int.TryParse(Environment.GetEnvironmentVariable("VERSEOPS_LANGUAGE"), out var lcid) ? lcid : 1033;

        // Body shape per PDF, Environment Provisioning - Provision New Environment.
        // displayName + environmentSku are the only Required fields; we add the
        // minimal linkedEnvironmentMetadata needed for a Sandbox that actually
        // creates a Dataverse org.
        var requestObj = new
        {
            displayName,
            environmentSku = "Sandbox",
            location,
            databaseType   = "CommonDataService",
            description    = "Created by VerseOps.SdkTests EnvironmentProvisioningTests (opt-in).",
            linkedEnvironmentMetadata = new
            {
                baseLanguage = language,
                currency = new { code = currency },
                templates = Array.Empty<string>(),
            }
        };
        var requestJson = JsonSerializer.Serialize(requestObj, new JsonSerializerOptions { WriteIndented = true });

        _out.WriteLine($"[CREATE] POST /provisioning/create  body:");
        _out.WriteLine(requestJson);

        var (status, body, headers) = await SendWithHeadersAsync(
            HttpMethod.Post,
            $"{PpacBase}/environmentmanagement/provisioning/create?api-version={ApiVersion}",
            requestJson).ConfigureAwait(false);

        LogResult("POST /provisioning/create", status, body);
        // Dump every response header to make diagnosis trivial when PPAC returns an
        // empty body (it sometimes 202s with no body, no operation-location, and
        // only a correlation id — we then have to fall back to env-list polling).
        foreach (var kv in headers)
            _out.WriteLine($"[CREATE] header: {kv.Key} = {kv.Value}");
        var opLocation =
            headers.TryGetValue("operation-location", out var ol) ? ol :
            headers.TryGetValue("location", out var loc) ? loc :
            null;
        var correlation = headers.TryGetValue("x-ms-correlation-id", out var c) ? c : null;
        if (opLocation != null)  _out.WriteLine($"[CREATE] operation-location: {opLocation}");
        if (correlation != null) _out.WriteLine($"[CREATE] x-ms-correlation-id: {correlation}");

        // Per docs: 201 Created (sync) or 202 Accepted (async, op-location populated).
        Assert.True((int)status == 201 || (int)status == 202,
            $"Unexpected status HTTP {(int)status}.\nBody: {body}");

        // ---------- AUTO-CLEANUP: poll op, extract env id, DELETE ----------
        // The test creates a real sandbox; leaving it lying around wastes a tenant slot.
        // We always try to delete it on the way out. Set VERSEOPS_KEEP_SANDBOX=1 to opt out.
        var keep = string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_KEEP_SANDBOX"), "1", StringComparison.Ordinal);
        if (keep)
        {
            _out.WriteLine("[CLEANUP] VERSEOPS_KEEP_SANDBOX=1 set — leaving sandbox in tenant.");
            return;
        }

        string? newEnvId = TryExtractEnvId(body);
        string? finalOpBody = null;
        if (newEnvId == null && opLocation != null)
        {
            finalOpBody = await PollOperationAsync(opLocation, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
            newEnvId = TryExtractEnvId(finalOpBody);
        }
        if (newEnvId == null)
        {
            // PPAC sometimes 202s with empty body + no op-location. Fall back to polling
            // the env list with backoff — newly created envs take 30s–3min to appear.
            newEnvId = await WaitForEnvIdByDisplayNameAsync(displayName, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        }

        Assert.False(string.IsNullOrWhiteSpace(newEnvId),
            $"Could not determine created environment id for cleanup. Create body: {body}\nFinal op body: {finalOpBody}");

        await DeleteEnvironmentAsync(newEnvId!, "CLEANUP").ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task PATCH_Link_Dataverse_To_Existing_Env()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_LINK_DV"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_LINK_DV=1 (and VERSEOPS_TARGET_ENV_ID) to actually link Dataverse to an existing env.");
        var envId = Environment.GetEnvironmentVariable("VERSEOPS_TARGET_ENV_ID");
        Skip.If(string.IsNullOrWhiteSpace(envId), "VERSEOPS_TARGET_ENV_ID is required for link-Dataverse test.");

        var currency = Environment.GetEnvironmentVariable("VERSEOPS_CURRENCY") ?? "USD";
        var language = int.TryParse(Environment.GetEnvironmentVariable("VERSEOPS_LANGUAGE"), out var lcid) ? lcid : 1033;

        var requestObj = new
        {
            baseLanguage = language,
            currency = new { code = currency },
            templates = Array.Empty<string>(),
        };
        var requestJson = JsonSerializer.Serialize(requestObj);

        _out.WriteLine($"[LINK] PATCH /provisioning/environments/{envId}/link  body={requestJson}");
        var (status, body, _) = await SendWithHeadersAsync(
            HttpMethod.Patch,
            $"{PpacBase}/environmentmanagement/provisioning/environments/{envId}/link?api-version={ApiVersion}",
            requestJson).ConfigureAwait(false);
        LogResult($"PATCH /provisioning/environments/{envId}/link", status, body);
        Assert.True((int)status == 202 || (int)status == 200,
            $"Unexpected status HTTP {(int)status}.\nBody: {body}");
    }

    // ---------- helpers ----------

    private async Task<(System.Net.HttpStatusCode status, string body)> SendAsync(HttpMethod method, string url, string? json = null)
    {
        var (s, b, _) = await SendWithHeadersAsync(method, url, json).ConfigureAwait(false);
        return (s, b);
    }

    private async Task<(System.Net.HttpStatusCode status, string body, Dictionary<string, string> headers)> SendWithHeadersAsync(
        HttpMethod method, string url, string? json)
    {
        var token = await _fx.Auth.GetTokenAsync(SdkAuthFixture.PpacScope, CancellationToken.None).ConfigureAwait(false);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json != null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers)
            headers[h.Key] = string.Join(", ", h.Value);
        // PPAC sometimes places operation-location / location on content headers; capture both.
        if (resp.Content?.Headers != null)
        {
            foreach (var h in resp.Content.Headers)
                if (!headers.ContainsKey(h.Key))
                    headers[h.Key] = string.Join(", ", h.Value);
        }
        return (resp.StatusCode, body, headers);
    }

    private void LogResult(string label, System.Net.HttpStatusCode status, string body)
    {
        _out.WriteLine($"=== {label}");
        _out.WriteLine($"HTTP {(int)status} {status}");
        _out.WriteLine("--- body (truncated) ---");
        _out.WriteLine(body.Length > 4000 ? body[..4000] + $"\n...({body.Length - 4000} more)" : body);
    }

    // Poll an Azure-style long-running operation URL until the response body shows a
    // terminal state (Succeeded / Failed / Cancelled / FailedToCreate / etc.) or we
    // hit the per-call timeout. Returns the final body so callers can extract IDs.
    private async Task<string> PollOperationAsync(string operationUrl, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromSeconds(5);
        string lastBody = "";
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            var (s, b, _) = await SendWithHeadersAsync(HttpMethod.Get, operationUrl, null).ConfigureAwait(false);
            lastBody = b;
            var bodyLower = b ?? "";
            _out.WriteLine($"[POLL] attempt {attempt}  HTTP {(int)s}  len={bodyLower.Length}");
            if ((int)s >= 400)
            {
                _out.WriteLine($"[POLL] non-success: {bodyLower}");
                return lastBody;
            }
            // Look for state field in the JSON. PPAC operation status objects use either
            // {"state":{"id":"Succeeded"}} or {"status":"Succeeded"}; we match both broadly.
            if (Regex.IsMatch(bodyLower, "\"(state|status)\"\\s*:\\s*(\\{[^}]*\"id\"\\s*:\\s*)?\"(Succeeded|Failed|Cancelled|FailedToCreate|Aborted)\"",
                    RegexOptions.IgnoreCase))
            {
                _out.WriteLine("[POLL] terminal state reached.");
                return lastBody;
            }
            await Task.Delay(delay).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(20)) delay += TimeSpan.FromSeconds(5);
        }
        _out.WriteLine("[POLL] timed out waiting for terminal state.");
        return lastBody;
    }

    // Extract the new environment id (GUID-shaped name) from any of the response shapes
    // PPAC uses: {"name":"<guid>"}, {"id":".../environments/<guid>"}, or
    // {"links":{"environment":{"path":".../environments/<guid>"}}}.
    private static string? TryExtractEnvId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var pathMatch = Regex.Match(body, "/environments/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
        if (pathMatch.Success) return pathMatch.Groups["id"].Value;
        var nameMatch = Regex.Match(body, "\"name\"\\s*:\\s*\"(?<id>[0-9a-fA-F-]{36})\"", RegexOptions.IgnoreCase);
        if (nameMatch.Success) return nameMatch.Groups["id"].Value;
        return null;
    }

    // Fallback: scan an environments collection for the matching displayName and pull
    // its name (the GUID-shaped id). Used when create response + op-status both lack an id.
    private static string? TryFindEnvIdByDisplayName(string? listBody, string displayName)
    {
        if (string.IsNullOrWhiteSpace(listBody) || string.IsNullOrWhiteSpace(displayName)) return null;
        try
        {
            using var doc = JsonDocument.Parse(listBody);
            if (!doc.RootElement.TryGetProperty("value", out var arr) &&
                !doc.RootElement.TryGetProperty("collection", out arr))
                return null;
            foreach (var env in arr.EnumerateArray())
            {
                string? dn = null;
                if (env.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("displayName", out var dnProp))
                    dn = dnProp.GetString();
                dn ??= env.TryGetProperty("displayName", out var dnRoot) ? dnRoot.GetString() : null;
                if (!string.Equals(dn, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                if (env.TryGetProperty("name", out var nameProp))
                    return nameProp.GetString();
                if (env.TryGetProperty("id", out var idProp))
                {
                    var idStr = idProp.GetString();
                    var m = Regex.Match(idStr ?? "", "/environments/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups["id"].Value;
                }
            }
        }
        catch (JsonException) { /* shape mismatch — give up */ }
        return null;
    }

    // Poll the env list looking for a freshly-created env by displayName. Newly-POSTed
    // envs typically don't appear in the listing for 30s–3min, hence the long deadline.
    private async Task<string?> WaitForEnvIdByDisplayNameAsync(string displayName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromSeconds(15);
        var listUrl = $"{PpacBase}/environmentmanagement/environments?api-version={ApiVersion}";
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            var (lsStatus, lsBody) = await SendAsync(HttpMethod.Get, listUrl).ConfigureAwait(false);
            var found = TryFindEnvIdByDisplayName(lsBody, displayName);
            _out.WriteLine($"[LIST-POLL] attempt {attempt}  HTTP {(int)lsStatus}  found={found ?? "(null)"}");
            if (!string.IsNullOrWhiteSpace(found)) return found;
            await Task.Delay(delay).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(30)) delay += TimeSpan.FromSeconds(5);
        }
        return null;
    }

    // Enumerate every env whose displayName starts with the given prefix. Returns
    // (envId, displayName) tuples extracted from either the 'value' or 'collection' array.
    private async Task<List<(string id, string displayName)>> ListEnvsByPrefixAsync(string prefix)
    {
        var listUrl = $"{PpacBase}/environmentmanagement/environments?api-version={ApiVersion}";
        var (lsStatus, lsBody) = await SendAsync(HttpMethod.Get, listUrl).ConfigureAwait(false);
        var hits = new List<(string id, string displayName)>();
        if ((int)lsStatus >= 400) return hits;
        try
        {
            using var doc = JsonDocument.Parse(lsBody);
            if (!doc.RootElement.TryGetProperty("value", out var arr) &&
                !doc.RootElement.TryGetProperty("collection", out arr))
                return hits;
            foreach (var env in arr.EnumerateArray())
            {
                string? dn = null;
                if (env.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("displayName", out var dnProp))
                    dn = dnProp.GetString();
                dn ??= env.TryGetProperty("displayName", out var dnRoot) ? dnRoot.GetString() : null;
                if (string.IsNullOrEmpty(dn) || !dn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string? id = null;
                if (env.TryGetProperty("name", out var nameProp)) id = nameProp.GetString();
                if (id == null && env.TryGetProperty("id", out var idProp))
                {
                    var m = Regex.Match(idProp.GetString() ?? "", "/environments/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
                    if (m.Success) id = m.Groups["id"].Value;
                }
                if (!string.IsNullOrEmpty(id)) hits.Add((id!, dn));
            }
        }
        catch (JsonException) { }
        return hits;
    }

    // Centralised DELETE so both the auto-cleanup and the orphan-sweeper share semantics.
    private async Task DeleteEnvironmentAsync(string envId, string tag)
    {
        _out.WriteLine($"[{tag}] DELETE /environments/{envId}");
        var (delStatus, delBody, delHeaders) = await SendWithHeadersAsync(
            HttpMethod.Delete,
            $"{PpacBase}/environmentmanagement/environments/{envId}?api-version={ApiVersion}",
            null).ConfigureAwait(false);
        LogResult($"DELETE /environments/{envId} ({tag})", delStatus, delBody);
        if (delHeaders.TryGetValue("operation-location", out var delOp))
            _out.WriteLine($"[{tag}] delete operation-location: {delOp}");
        Assert.True((int)delStatus is 200 or 202 or 204,
            $"DELETE /environments/{envId} failed: HTTP {(int)delStatus}. Body: {delBody}");
    }

    // ---------- ORPHAN SWEEPER ----------
    // Defensive: if a previous create test failed before cleanup, this scoops up every
    // env whose displayName starts with "verseops-sandbox-" and deletes it. Gated so a
    // routine run can't accidentally delete a test sandbox someone is using.
    [SkippableFact]
    public async Task Cleanup_Orphan_VerseOps_Sandboxes()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_CLEANUP_SANDBOXES"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_CLEANUP_SANDBOXES=1 to sweep verseops-sandbox-* environments.");

        var prefix = Environment.GetEnvironmentVariable("VERSEOPS_SANDBOX_PREFIX") ?? "verseops-sandbox-";

        // Newly-POSTed envs may take minutes to surface in the list. Retry a bit
        // before declaring there is nothing to clean.
        List<(string id, string displayName)> hits = new();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        var delay = TimeSpan.FromSeconds(15);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            hits = await ListEnvsByPrefixAsync(prefix).ConfigureAwait(false);
            _out.WriteLine($"[SWEEP] attempt {attempt}  prefix='{prefix}'  matches={hits.Count}");
            if (hits.Count > 0) break;
            await Task.Delay(delay).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(30)) delay += TimeSpan.FromSeconds(5);
        }

        if (hits.Count == 0)
        {
            _out.WriteLine($"[SWEEP] nothing to clean — no env starts with '{prefix}'.");
            return;
        }

        var failures = new List<string>();
        foreach (var hit in hits)
        {
            _out.WriteLine($"[SWEEP] deleting {hit.id}  displayName='{hit.displayName}'");
            try { await DeleteEnvironmentAsync(hit.id, "SWEEP").ConfigureAwait(false); }
            catch (Exception ex) { failures.Add($"{hit.id} ({hit.displayName}): {ex.Message}"); }
        }
        Assert.True(failures.Count == 0, $"Sweep had failures:\n  " + string.Join("\n  ", failures));
    }
}
