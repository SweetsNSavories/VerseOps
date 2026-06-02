using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Direct-HTTP coverage for the PPAC environment lifecycle surface
/// (Power Platform API v2024-10-01) — Recover, Restore (candidates + execute),
/// and State (disable + enable). These routes are not yet exposed by the
/// Microsoft.PowerPlatform.Management 2.0.3317.207 NuGet SDK and must be
/// hit via raw HttpClient.
///
/// Five endpoints covered:
///   POST /environments/{environmentId}/recover                          (Recover Environment)
///   GET  /environments/{sourceEnvironmentId}/restoreCandidates          (Get Restore Candidates)
///   POST /environments/{targetEnvironmentId}/Restore                    (Restore Environment)
///   POST /environments/{environmentId}/Disable                          (Disable Environment)
///   POST /environments/{environmentId}/Enable                           (Enable Environment)
///
/// All POST endpoints support <c>ValidateOnly=true</c> per the Microsoft Learn
/// reference. We exploit that to exercise the routes without mutating live
/// environments by default. Real (destructive) execution is gated on opt-in
/// env vars per test.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentLifecycleTests
{
    private const string PpacBase   = "https://api.powerplatform.com";
    private const string ApiVersion = "2024-10-01";

    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentLifecycleTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    // ---------- RECOVER ----------

    [SkippableFact]
    public async Task POST_Recover_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        // Recover targets a *deleted* env. We can't know one a-priori, so we use
        // the fixture-seeded existing env id with ValidateOnly=true; the API
        // will return either 202 (queued validate) or 400/404 with a
        // ValidationResponse explaining why this env is not recoverable.
        // That is *expected behavior* and proves the route is reachable.
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/recover" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Post, url, json: "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/recover?ValidateOnly=true", status, body);

        // Accept 200/202 (queued) OR 400/404 (validation rejected because env is not deleted).
        // 5xx or 401/403 means a real failure.
        var sc = (int)status;
        Assert.True(sc is 200 or 202 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_Recover_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_RECOVER_DELETED_ENV"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_RECOVER_DELETED_ENV=1 (and VERSEOPS_DELETED_ENV_ID) to actually recover a deleted environment.");
        var envId = Environment.GetEnvironmentVariable("VERSEOPS_DELETED_ENV_ID");
        Skip.If(string.IsNullOrWhiteSpace(envId), "VERSEOPS_DELETED_ENV_ID is required for real Recover.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/recover?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/recover (REAL)", status, body);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[RECOVER] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[RECOVER] x-ms-correlation-id: {c}");
        Assert.Equal(202, (int)status);
    }

    // ---------- RESTORE ----------

    [SkippableFact]
    public async Task GET_RestoreCandidates_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/restoreCandidates?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /environments/{envId}/restoreCandidates", status, body);

        // 200 (candidates returned) or 400/404 if source has no backups / not Dataverse-linked.
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_Restore_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        // ValidateOnly avoids actually restoring. A synthetic restorePointDateTime
        // + sourceEnvironmentId is used; we expect 400 with a structured
        // ValidationResponse OR 202 if the API skips deep validation in this mode.
        var requestObj = new
        {
            sourceEnvironmentId  = envId,
            restorePointDateTime = DateTimeOffset.UtcNow.AddHours(-1).ToString("o"),
            skipAuditData        = true,
        };
        var requestJson = JsonSerializer.Serialize(requestObj);

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/Restore" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Post, url, requestJson).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/Restore?ValidateOnly=true", status, body);

        var sc = (int)status;
        Assert.True(sc is 200 or 202 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_Restore_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_RESTORE_ENV"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_RESTORE_ENV=1 plus VERSEOPS_RESTORE_TARGET_ID, VERSEOPS_RESTORE_SOURCE_ID, VERSEOPS_RESTORE_POINT to actually restore.");
        var targetId = Environment.GetEnvironmentVariable("VERSEOPS_RESTORE_TARGET_ID");
        var sourceId = Environment.GetEnvironmentVariable("VERSEOPS_RESTORE_SOURCE_ID");
        var rp       = Environment.GetEnvironmentVariable("VERSEOPS_RESTORE_POINT");
        Skip.If(string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(rp),
            "VERSEOPS_RESTORE_TARGET_ID, _SOURCE_ID, _POINT all required for real Restore.");

        var requestObj = new
        {
            sourceEnvironmentId  = sourceId,
            restorePointDateTime = rp,
            skipAuditData        = false,
        };
        var requestJson = JsonSerializer.Serialize(requestObj);

        var url = $"{PpacBase}/environmentmanagement/environments/{targetId}/Restore?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, requestJson).ConfigureAwait(false);
        LogResult($"POST /environments/{targetId}/Restore (REAL)", status, body);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[RESTORE] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[RESTORE] x-ms-correlation-id: {c}");
        Assert.Equal(202, (int)status);
    }

    // ---------- STATE (Disable / Enable) ----------

    [SkippableFact]
    public async Task POST_Disable_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var body = JsonSerializer.Serialize(new { reason = "VerseOps SDK test ValidateOnly probe" });
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/Disable" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, respBody) = await SendAsync(HttpMethod.Post, url, body).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/Disable?ValidateOnly=true", status, respBody);

        var sc = (int)status;
        Assert.True(sc is 200 or 202 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {respBody}");
    }

    [SkippableFact]
    public async Task POST_Enable_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var body = JsonSerializer.Serialize(new { reason = "VerseOps SDK test ValidateOnly probe" });
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/Enable" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, respBody) = await SendAsync(HttpMethod.Post, url, body).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/Enable?ValidateOnly=true", status, respBody);

        var sc = (int)status;
        Assert.True(sc is 200 or 202 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {respBody}");
    }

    [SkippableFact]
    public async Task POST_Disable_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_STATE_TOGGLE"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_STATE_TOGGLE=1 + VERSEOPS_STATE_TARGET_ID to actually disable an environment.");
        var envId = Environment.GetEnvironmentVariable("VERSEOPS_STATE_TARGET_ID");
        Skip.If(string.IsNullOrWhiteSpace(envId), "VERSEOPS_STATE_TARGET_ID is required.");

        var body = JsonSerializer.Serialize(new { reason = "VerseOps SDK test real disable" });
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/Disable?api-version={ApiVersion}";
        var (status, respBody, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, body).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/Disable (REAL)", status, respBody);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[DISABLE] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[DISABLE] x-ms-correlation-id: {c}");
        Assert.Equal(202, (int)status);
    }

    [SkippableFact]
    public async Task POST_Enable_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_STATE_TOGGLE"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_STATE_TOGGLE=1 + VERSEOPS_STATE_TARGET_ID to actually re-enable an environment.");
        var envId = Environment.GetEnvironmentVariable("VERSEOPS_STATE_TARGET_ID");
        Skip.If(string.IsNullOrWhiteSpace(envId), "VERSEOPS_STATE_TARGET_ID is required.");

        var body = JsonSerializer.Serialize(new { reason = "VerseOps SDK test real enable" });
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/Enable?api-version={ApiVersion}";
        var (status, respBody, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, body).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/Enable (REAL)", status, respBody);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[ENABLE] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[ENABLE] x-ms-correlation-id: {c}");
        Assert.Equal(202, (int)status);
    }

    // ---------- helpers ----------

    private string? ResolveEnvId()
    {
        // Prefer explicit override; otherwise fall back to the indexer seed
        // populated by SdkAuthFixture warm-up. Either is fine — we are just
        // pointing the route at a real GUID that the tenant knows about.
        var explicitId = Environment.GetEnvironmentVariable("VERSEOPS_TARGET_ENV_ID");
        if (!string.IsNullOrWhiteSpace(explicitId)) return explicitId;
        return _fx.TryGetIndexerSeed("Environments", out var seeded) ? seeded : null;
    }

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
        return (resp.StatusCode, body, headers);
    }

    private void LogResult(string label, System.Net.HttpStatusCode status, string body)
    {
        _out.WriteLine($"=== {label}");
        _out.WriteLine($"HTTP {(int)status} {status}");
        _out.WriteLine("--- body (truncated) ---");
        _out.WriteLine(body.Length > 4000 ? body[..4000] + $"\n...({body.Length - 4000} more)" : body);
    }
}
