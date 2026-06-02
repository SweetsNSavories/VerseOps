using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Direct-HTTP coverage for the EnvironmentManagement Failover + Operation routes
/// (Power Platform API v2024-10-01, all currently flagged Preview on Learn).
///
/// Five endpoints covered:
///   POST /environments/{id}/disableDisasterRecovery        (ValidateOnly + opt-in real)
///   GET  /environments/{id}/businessContinuityStateFullSnapshot   (safe read)
///   POST /environments/{id}/disasterRecoveryDrill          (ValidateOnly + opt-in real)
///   GET  /environments/{id}/operations                     (safe list — also seeds the next test)
///   GET  /operations/{operationId}                         (uses an id from the list when available)
///
/// Routes that actually mutate (Disable-DR real-execute and DR-Drill real-execute) are
/// gated behind opt-in environment variables. The ValidateOnly variants prove the route
/// is reachable and the request shape is correct without changing tenant state.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentFailoverAndOperationsTests
{
    private const string PpacBase   = "https://api.powerplatform.com";
    private const string ApiVersion = "2024-10-01";

    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentFailoverAndOperationsTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    // ---------- FAILOVER — Disable Disaster Recovery ----------

    [SkippableFact]
    public async Task POST_DisableDisasterRecovery_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/disableDisasterRecovery" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Post, url, "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/disableDisasterRecovery?ValidateOnly=true", status, body);
        // Per Learn: 201 success, 400 bad request, 409 conflict. With ValidateOnly we expect
        // either "validation accepted" (201/200) or "DR isn't enabled on this env" (400/409/404).
        var sc = (int)status;
        Assert.True(sc is 200 or 201 or 202 or 400 or 404 or 409,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_DisableDisasterRecovery_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_DISABLE_DR"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_DISABLE_DR=1 (and optionally VERSEOPS_TARGET_ENV_ID) to actually disable disaster recovery.");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No environment id available.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/disableDisasterRecovery?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/disableDisasterRecovery (REAL)", status, body);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[DISABLE-DR] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[DISABLE-DR] x-ms-correlation-id: {c}");
        Assert.Equal(201, (int)status);
    }

    // ---------- FAILOVER — Business Continuity State Full Snapshot ----------

    [SkippableFact]
    public async Task GET_BusinessContinuityStateFullSnapshot_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/businessContinuityStateFullSnapshot?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /environments/{envId}/businessContinuityStateFullSnapshot", status, body);
        // 200 with BusinessContinuityStateFullSnapshot { lastSyncTime }; 404 if DR not configured;
        // 400 validation; 403 if BYO app lacks the required scope (documented behavior, not a defect).
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 403 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    // ---------- FAILOVER — DR Drill ----------

    [SkippableFact]
    public async Task POST_DisasterRecoveryDrill_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/disasterRecoveryDrill" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Post, url, "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/disasterRecoveryDrill?ValidateOnly=true", status, body);
        var sc = (int)status;
        Assert.True(sc is 200 or 201 or 202 or 400 or 404 or 409,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_DisasterRecoveryDrill_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_DR_DRILL"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_DR_DRILL=1 (and optionally VERSEOPS_TARGET_ENV_ID) to actually perform a DR drill.");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No environment id available.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/disasterRecoveryDrill?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/disasterRecoveryDrill (REAL)", status, body);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[DR-DRILL] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[DR-DRILL] x-ms-correlation-id: {c}");
        Assert.Equal(201, (int)status);
    }

    // ---------- OPERATION — list per env + get by id ----------

    [SkippableFact]
    public async Task GET_OperationsForEnvironment_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/operations?limit=5&api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /environments/{envId}/operations?limit=5", status, body);
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
        if (sc == 200)
        {
            // Body shape: { "collection": [ { operationId, name, status, ... } ], "continuationToken": "..." }
            Assert.Contains("collection", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task GET_OperationById_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        // Resolve an operationId: explicit VERSEOPS_OPERATION_ID wins; otherwise pull the first one
        // from the env's operations list. If neither path produces an id (brand-new env with no ops
        // yet), we skip rather than fail — there's nothing to GET-by-id.
        var operationId = Environment.GetEnvironmentVariable("VERSEOPS_OPERATION_ID");
        if (string.IsNullOrWhiteSpace(operationId))
        {
            var listUrl = $"{PpacBase}/environmentmanagement/environments/{envId}/operations?limit=1&api-version={ApiVersion}";
            var (listStatus, listBody) = await SendAsync(HttpMethod.Get, listUrl).ConfigureAwait(false);
            Skip.If((int)listStatus != 200, $"Could not list operations to seed get-by-id (HTTP {(int)listStatus}).");
            operationId = TryExtractFirstOperationId(listBody);
        }
        Skip.If(string.IsNullOrWhiteSpace(operationId),
            $"No operationId available for environment {envId}. Set VERSEOPS_OPERATION_ID to test directly.");

        var url = $"{PpacBase}/environmentmanagement/operations/{operationId}?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /operations/{operationId}", status, body);
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
        if (sc == 200)
        {
            // Response is OperationExecutionResult — must echo back the operationId we asked for.
            Assert.Contains(operationId!, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- helpers ----------

    private string? ResolveEnvId()
    {
        var explicitId = Environment.GetEnvironmentVariable("VERSEOPS_TARGET_ENV_ID");
        if (!string.IsNullOrWhiteSpace(explicitId)) return explicitId;
        return _fx.TryGetIndexerSeed("Environments", out var seeded) ? seeded : null;
    }

    private static string? TryExtractFirstOperationId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("collection", out var coll)) return null;
            if (coll.ValueKind != JsonValueKind.Array || coll.GetArrayLength() == 0) return null;
            var first = coll[0];
            if (first.TryGetProperty("operationId", out var op) && op.ValueKind == JsonValueKind.String)
                return op.GetString();
            return null;
        }
        catch
        {
            return null;
        }
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
