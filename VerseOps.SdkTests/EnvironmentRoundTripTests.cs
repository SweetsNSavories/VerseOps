using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// End-to-end "create → mutate → tear down" smoke for the PPAC environment lifecycle
/// surface. Provisions one or two fresh sandboxes, exercises every documented op
/// (backup, disable, enable, copy, restore, delete, recover), then ALWAYS cleans up
/// in a finally block.
///
/// Hard-gated: only fires when <c>VERSEOPS_LIFECYCLE_ROUNDTRIP=1</c>. Each route is
/// also individually toggleable via <c>VERSEOPS_LIFECYCLE_SKIP_{BACKUP|DISABLE|COPY|RESTORE|RECOVER}=1</c>
/// so a partial-coverage run is possible without editing source.
///
/// Set <c>VERSEOPS_LIFECYCLE_KEEP=1</c> to opt out of the finally-block tear-down
/// (handy when debugging a specific failure and you want the sandbox to stick around).
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentRoundTripTests
{
    private const string PpacBase   = "https://api.powerplatform.com";
    private const string ApiVersion = "2024-10-01";

    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentRoundTripTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    [SkippableFact]
    public async Task RoundTrip_NewSandbox_Lifecycle()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_LIFECYCLE_ROUNDTRIP"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_LIFECYCLE_ROUNDTRIP=1 to provision sandboxes and exercise the full lifecycle.");

        var keep      = Flag("VERSEOPS_LIFECYCLE_KEEP");
        var skipBak   = Flag("VERSEOPS_LIFECYCLE_SKIP_BACKUP");
        var skipDis   = Flag("VERSEOPS_LIFECYCLE_SKIP_DISABLE");
        var skipCopy  = Flag("VERSEOPS_LIFECYCLE_SKIP_COPY");
        var skipRest  = Flag("VERSEOPS_LIFECYCLE_SKIP_RESTORE");
        var skipRecov = Flag("VERSEOPS_LIFECYCLE_SKIP_RECOVER");

        var location = Environment.GetEnvironmentVariable("VERSEOPS_LOCATION") ?? "unitedstates";
        var currency = Environment.GetEnvironmentVariable("VERSEOPS_CURRENCY") ?? "USD";
        var language = int.TryParse(Environment.GetEnvironmentVariable("VERSEOPS_LANGUAGE"), out var lcid) ? lcid : 1033;

        string? envA = null, envB = null, backupId = null;
        var failures = new List<string>();

        try
        {
            // ---------- Provision sandbox A (primary) ----------
            var aDisplay = $"verseops-lifecycle-a-{DateTime.UtcNow:yyyyMMddHHmmss}";
            envA = await ProvisionSandboxAsync(aDisplay, location, currency, language).ConfigureAwait(false);
            _out.WriteLine($"[ROUND] env A provisioned: {envA}");

            // ---------- BACKUP ----------
            if (!skipBak)
            {
                try
                {
                    backupId = await CreateBackupAsync(envA!).ConfigureAwait(false);
                    _out.WriteLine($"[ROUND] backup created on A: {backupId ?? "(no id)"}");
                }
                catch (Exception ex) { failures.Add($"BACKUP: {ex.Message}"); }
            }

            // ---------- DISABLE / ENABLE ----------
            if (!skipDis)
            {
                try
                {
                    await StateToggleAsync(envA!, "Disable", "VerseOps lifecycle round-trip disable").ConfigureAwait(false);
                    await StateToggleAsync(envA!, "Enable",  "VerseOps lifecycle round-trip enable").ConfigureAwait(false);
                }
                catch (Exception ex) { failures.Add($"DISABLE/ENABLE: {ex.Message}"); }
            }

            // ---------- COPY (needs target sandbox B) ----------
            if (!skipCopy)
            {
                try
                {
                    var bDisplay = $"verseops-lifecycle-b-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    envB = await ProvisionSandboxAsync(bDisplay, location, currency, language).ConfigureAwait(false);
                    _out.WriteLine($"[ROUND] env B (copy target) provisioned: {envB}");
                    await CopyAsync(sourceId: envA!, targetId: envB!).ConfigureAwait(false);
                }
                catch (Exception ex) { failures.Add($"COPY: {ex.Message}"); }
            }

            // ---------- RESTORE (needs a backup taken above) ----------
            if (!skipRest && backupId != null)
            {
                try
                {
                    // Restore A from its own backup; PPAC accepts source==target if the
                    // restorePointDateTime falls inside the source env's retention window.
                    var rp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o");
                    await RestoreAsync(targetId: envA!, sourceId: envA!, restorePoint: rp).ConfigureAwait(false);
                }
                catch (Exception ex) { failures.Add($"RESTORE: {ex.Message}"); }
            }

            // ---------- DELETE BACKUP ----------
            if (backupId != null)
            {
                try { await DeleteBackupAsync(envA!, backupId).ConfigureAwait(false); }
                catch (Exception ex) { failures.Add($"DELETE-BACKUP: {ex.Message}"); }
            }

            // ---------- SOFT DELETE + RECOVER ----------
            if (!skipRecov)
            {
                try
                {
                    await DeleteEnvironmentAsync(envA!, "ROUND-SOFT-DELETE").ConfigureAwait(false);
                    // Soft-delete completes async; wait briefly so /recover sees it in the "deleted" pool.
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    await RecoverAsync(envA!).ConfigureAwait(false);
                }
                catch (Exception ex) { failures.Add($"DELETE+RECOVER: {ex.Message}"); }
            }
        }
        finally
        {
            if (keep)
            {
                _out.WriteLine("[ROUND] VERSEOPS_LIFECYCLE_KEEP=1 set — leaving envs A and B in tenant.");
            }
            else
            {
                // Best-effort cleanup of BOTH envs. Swallow exceptions per-env so a
                // failure on A doesn't block the cleanup of B (and vice versa).
                foreach (var (id, tag) in new[] { (envA, "FINAL-A"), (envB, "FINAL-B") })
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    try { await DeleteEnvironmentAsync(id!, tag).ConfigureAwait(false); }
                    catch (Exception ex) { _out.WriteLine($"[{tag}] cleanup swallowed: {ex.Message}"); }
                }
            }
        }

        // Test passes if at least the create succeeded and no SUBSEQUENT step threw.
        // Individual op failures are surfaced as a single combined assertion message.
        Assert.True(failures.Count == 0,
            "Lifecycle steps failed:\n  " + string.Join("\n  ", failures));
    }

    // ===================================================================
    // Step helpers — each issues one PPAC request and waits for terminal state.
    // ===================================================================

    private async Task<string> ProvisionSandboxAsync(string displayName, string location, string currency, int language)
    {
        var requestObj = new
        {
            displayName,
            environmentSku = "Sandbox",
            location,
            databaseType   = "CommonDataService",
            description    = "VerseOps RoundTrip lifecycle test (auto-deleted).",
            linkedEnvironmentMetadata = new
            {
                baseLanguage = language,
                currency = new { code = currency },
                templates = Array.Empty<string>(),
            }
        };
        var json = JsonSerializer.Serialize(requestObj);
        _out.WriteLine($"[PROVISION] POST /provisioning/create displayName={displayName}");
        var (status, body, headers) = await SendWithHeadersAsync(
            HttpMethod.Post,
            $"{PpacBase}/environmentmanagement/provisioning/create?api-version={ApiVersion}",
            json).ConfigureAwait(false);
        LogResult("POST /provisioning/create", status, body);
        var sc = (int)status;
        Assert.True(sc == 201 || sc == 202, $"create returned HTTP {sc}; body: {body}");

        var opLocation =
            headers.TryGetValue("operation-location", out var ol) ? ol :
            headers.TryGetValue("location", out var loc) ? loc :
            null;

        string? envId = TryExtractEnvId(body);
        string? finalOpBody = null;
        if (envId == null && opLocation != null)
        {
            finalOpBody = await PollOperationAsync(opLocation, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
            envId = TryExtractEnvId(finalOpBody);
        }
        envId ??= await WaitForEnvIdByDisplayNameAsync(displayName, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        Assert.False(string.IsNullOrWhiteSpace(envId),
            $"could not resolve env id for displayName '{displayName}'. final op body: {finalOpBody}");
        return envId!;
    }

    private async Task<string?> CreateBackupAsync(string envId)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var json = JsonSerializer.Serialize(new { label = $"verseops-roundtrip-{stamp}" });
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/backups?api-version={ApiVersion}";
        var (s, b, h) = await SendWithHeadersAsync(HttpMethod.Post, url, json).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/backups", s, b);
        Assert.True((int)s is 200 or 201 or 202, $"create-backup returned HTTP {(int)s}; body: {b}");
        // backup response may include the backup id under various keys; pull anything GUID-shaped.
        var m = Regex.Match(b ?? "", "\"(backupId|id|name)\"\\s*:\\s*\"(?<id>[0-9a-fA-F-]{36})\"",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups["id"].Value;
        // also try op-location → poll → extract
        if (h.TryGetValue("operation-location", out var ol))
        {
            var finalOp = await PollOperationAsync(ol, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
            var m2 = Regex.Match(finalOp ?? "", "\"(backupId|id|name)\"\\s*:\\s*\"(?<id>[0-9a-fA-F-]{36})\"",
                RegexOptions.IgnoreCase);
            if (m2.Success) return m2.Groups["id"].Value;
        }
        return null;
    }

    private async Task DeleteBackupAsync(string envId, string backupId)
    {
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/backups/{backupId}?api-version={ApiVersion}";
        var (s, b, _) = await SendWithHeadersAsync(HttpMethod.Delete, url, null).ConfigureAwait(false);
        LogResult($"DELETE /environments/{envId}/backups/{backupId}", s, b);
        Assert.True((int)s is 200 or 202 or 204, $"delete-backup returned HTTP {(int)s}; body: {b}");
    }

    private async Task StateToggleAsync(string envId, string verb, string reason)
    {
        var json = JsonSerializer.Serialize(new { reason });
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/{verb}?api-version={ApiVersion}";
        var (s, b, h) = await SendWithHeadersAsync(HttpMethod.Post, url, json).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/{verb}", s, b);
        Assert.True((int)s is 200 or 202, $"{verb} returned HTTP {(int)s}; body: {b}");
        if (h.TryGetValue("operation-location", out var ol))
            await PollOperationAsync(ol, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
    }

    private async Task CopyAsync(string sourceId, string targetId)
    {
        var json = JsonSerializer.Serialize(new
        {
            sourceEnvironmentId = sourceId,
            copyType            = "MinimalCopy", // schema-only; faster + cheaper than FullCopy
            skipAuditData       = true,
        });
        var url = $"{PpacBase}/environmentmanagement/environments/{targetId}/copy?api-version={ApiVersion}";
        var (s, b, h) = await SendWithHeadersAsync(HttpMethod.Post, url, json).ConfigureAwait(false);
        LogResult($"POST /environments/{targetId}/copy", s, b);
        Assert.True((int)s is 200 or 202, $"copy returned HTTP {(int)s}; body: {b}");
        if (h.TryGetValue("operation-location", out var ol))
            await PollOperationAsync(ol, TimeSpan.FromMinutes(20)).ConfigureAwait(false);
    }

    private async Task RestoreAsync(string targetId, string sourceId, string restorePoint)
    {
        var json = JsonSerializer.Serialize(new
        {
            sourceEnvironmentId  = sourceId,
            restorePointDateTime = restorePoint,
            skipAuditData        = true,
        });
        var url = $"{PpacBase}/environmentmanagement/environments/{targetId}/Restore?api-version={ApiVersion}";
        var (s, b, h) = await SendWithHeadersAsync(HttpMethod.Post, url, json).ConfigureAwait(false);
        LogResult($"POST /environments/{targetId}/Restore", s, b);
        Assert.True((int)s is 200 or 202, $"restore returned HTTP {(int)s}; body: {b}");
        if (h.TryGetValue("operation-location", out var ol))
            await PollOperationAsync(ol, TimeSpan.FromMinutes(20)).ConfigureAwait(false);
    }

    private async Task RecoverAsync(string envId)
    {
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/recover?api-version={ApiVersion}";
        var (s, b, h) = await SendWithHeadersAsync(HttpMethod.Post, url, "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/recover", s, b);
        Assert.True((int)s is 200 or 202, $"recover returned HTTP {(int)s}; body: {b}");
        if (h.TryGetValue("operation-location", out var ol))
            await PollOperationAsync(ol, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
    }

    private async Task DeleteEnvironmentAsync(string envId, string tag)
    {
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}?api-version={ApiVersion}";
        var (s, b, h) = await SendWithHeadersAsync(HttpMethod.Delete, url, null).ConfigureAwait(false);
        LogResult($"DELETE /environments/{envId} ({tag})", s, b);
        Assert.True((int)s is 200 or 202 or 204, $"DELETE returned HTTP {(int)s}; body: {b}");
        if (h.TryGetValue("operation-location", out var ol))
            await PollOperationAsync(ol, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
    }

    // ===================================================================
    // Polling / extraction utilities (mirror EnvironmentProvisioningTests).
    // ===================================================================

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
            _out.WriteLine($"[POLL] attempt {attempt}  HTTP {(int)s}  len={(b?.Length ?? 0)}");
            if ((int)s >= 400) { _out.WriteLine($"[POLL] non-success: {b}"); return lastBody; }
            if (Regex.IsMatch(b ?? "",
                "\"(state|status)\"\\s*:\\s*(\\{[^}]*\"id\"\\s*:\\s*)?\"(Succeeded|Failed|Cancelled|FailedToCreate|Aborted)\"",
                RegexOptions.IgnoreCase))
            {
                _out.WriteLine("[POLL] terminal state reached.");
                return lastBody;
            }
            await Task.Delay(delay).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(20)) delay += TimeSpan.FromSeconds(5);
        }
        _out.WriteLine("[POLL] timed out.");
        return lastBody;
    }

    private static string? TryExtractEnvId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var path = Regex.Match(body, "/environments/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
        if (path.Success) return path.Groups["id"].Value;
        var name = Regex.Match(body, "\"name\"\\s*:\\s*\"(?<id>[0-9a-fA-F-]{36})\"", RegexOptions.IgnoreCase);
        if (name.Success) return name.Groups["id"].Value;
        return null;
    }

    private async Task<string?> WaitForEnvIdByDisplayNameAsync(string displayName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromSeconds(15);
        var listUrl = $"{PpacBase}/environmentmanagement/environments?api-version={ApiVersion}";
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            var (_, body, _) = await SendWithHeadersAsync(HttpMethod.Get, listUrl, null).ConfigureAwait(false);
            var hit = FindEnvIdByDisplayName(body, displayName);
            _out.WriteLine($"[LIST-POLL] {displayName} attempt {attempt} hit={hit ?? "(null)"}");
            if (!string.IsNullOrWhiteSpace(hit)) return hit;
            await Task.Delay(delay).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(30)) delay += TimeSpan.FromSeconds(5);
        }
        return null;
    }

    private static string? FindEnvIdByDisplayName(string? listBody, string displayName)
    {
        if (string.IsNullOrWhiteSpace(listBody)) return null;
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
                    props.TryGetProperty("displayName", out var dnProp)) dn = dnProp.GetString();
                dn ??= env.TryGetProperty("displayName", out var dnRoot) ? dnRoot.GetString() : null;
                if (!string.Equals(dn, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                if (env.TryGetProperty("name", out var nameProp)) return nameProp.GetString();
                if (env.TryGetProperty("id", out var idProp))
                {
                    var m = Regex.Match(idProp.GetString() ?? "", "/environments/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups["id"].Value;
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    private async Task<(System.Net.HttpStatusCode status, string body, Dictionary<string, string> headers)> SendWithHeadersAsync(
        HttpMethod method, string url, string? json)
    {
        var token = await _fx.Auth.GetTokenAsync(SdkAuthFixture.PpacScope, CancellationToken.None).ConfigureAwait(false);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
        if (resp.Content?.Headers != null)
            foreach (var h in resp.Content.Headers)
                if (!headers.ContainsKey(h.Key)) headers[h.Key] = string.Join(", ", h.Value);
        return (resp.StatusCode, body, headers);
    }

    private void LogResult(string label, System.Net.HttpStatusCode status, string body)
    {
        _out.WriteLine($"=== {label}");
        _out.WriteLine($"HTTP {(int)status} {status}");
        _out.WriteLine(body == null ? "(null body)"
            : body.Length > 3000 ? body[..3000] + $"\n...({body.Length - 3000} more)" : body);
    }

    private static bool Flag(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
}
