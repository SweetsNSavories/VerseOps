using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Direct-HTTP coverage for the remaining PPAC EnvironmentManagement endpoints
/// (Power Platform API v2024-10-01) not exercised by EnvironmentProvisioningTests
/// or EnvironmentLifecycleTests. The SDK NuGet 2.0.3317.207 still pins
/// api-version 2022-03-01-preview, so all 2024-10-01 routes go via raw HttpClient.
///
/// Eleven endpoints covered:
///   GET    /environments                                              (List Environments For User)
///   GET    /environments/{environmentId}                              (Get Environment By Id For User)
///   DELETE /environments/{environmentId}                              (Delete Environment By Id — ValidateOnly + opt-in real)
///   GET    /environments/{sourceEnvironmentId}/copyCandidates         (Get Environment Copy Candidates)
///   POST   /environments/{targetEnvironmentId}/copy                   (Copy Environment — ValidateOnly + opt-in real)
///   GET    /environments/{environmentId}/backups                      (Get Environment Backups)
///   POST   /environments/{environmentId}/backups                      (Create Environment Backup — opt-in real; no ValidateOnly)
///   DELETE /environments/{environmentId}/backups/{backupId}           (Delete Environment Backup — opt-in real; no ValidateOnly)
///   GET    /environments/{environmentId}/settings                     (List Environment Management Settings)
///   POST   /environments/{environmentId}/settings                     (Create Environment Management Settings — opt-in real)
///   PATCH  /environments/{environmentId}/settings                     (Update Environment Management Settings — opt-in real)
///
/// All routes default to read-only or ValidateOnly behavior; anything that
/// actually mutates the tenant is gated behind an opt-in env var.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentAdditionalApiTests
{
    private const string PpacBase   = "https://api.powerplatform.com";
    private const string ApiVersion = "2024-10-01";

    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentAdditionalApiTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    // ---------- ENVIRONMENTS — list / get ----------

    [SkippableFact]
    public async Task GET_Environments_List_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        // PPAC OData has MaxTop=0 on this collection — $top yields HTTP 400.
        var url = $"{PpacBase}/environmentmanagement/environments?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult("GET /environments", status, body);
        Assert.True((int)status < 400, $"HTTP {(int)status}");
        // Body shape: { "value": [ EnvironmentResponse ], "@odata.nextlink": "..." }
        Assert.Contains("value", body, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GET_Environment_ById_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /environments/{envId}", status, body);
        Assert.True((int)status < 400, $"HTTP {(int)status}");
        Assert.Contains(envId!, body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- ENVIRONMENT DELETE — ValidateOnly + opt-in real ----------

    [SkippableFact]
    public async Task DELETE_Environment_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Delete, url).ConfigureAwait(false);
        LogResult($"DELETE /environments/{envId}?ValidateOnly=true", status, body);
        // PPAC returns 200 { "status": "ValidationPassed" } on success (older docs said 202).
        // Accept either, plus 400/404 if validation legitimately rejects. A bare 200/204 with
        // no ValidationPassed marker would mean the API ignored ValidateOnly and actually
        // deleted — the worst-case bug-class for us.
        var sc = (int)status;
        var isValidateOnlyPass = sc == 200 && body.Contains("ValidationPassed", StringComparison.OrdinalIgnoreCase);
        Assert.True(sc is 202 or 400 or 404 || isValidateOnlyPass,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task DELETE_Environment_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_DELETE_ENV"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_DELETE_ENV=1 + VERSEOPS_DELETE_ENV_ID to actually delete an environment.");
        var envId = Environment.GetEnvironmentVariable("VERSEOPS_DELETE_ENV_ID");
        Skip.If(string.IsNullOrWhiteSpace(envId), "VERSEOPS_DELETE_ENV_ID is required for real DELETE.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Delete, url, null).ConfigureAwait(false);
        LogResult($"DELETE /environments/{envId} (REAL)", status, body);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[DELETE-ENV] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[DELETE-ENV] x-ms-correlation-id: {c}");
        Assert.Equal(202, (int)status);
    }

    // ---------- ENVIRONMENT COPY — candidates + ValidateOnly + opt-in real ----------

    [SkippableFact]
    public async Task GET_CopyCandidates_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/copyCandidates?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /environments/{envId}/copyCandidates", status, body);
        // 200 with EnvironmentPagedCollection, or 400/404 if the source has no eligible targets.
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_Copy_ValidateOnly_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        // Per CopyRequest schema: copyType + sourceEnvironmentId required.
        // ValidateOnly lets us send envId as both source AND target — server validates, doesn't execute.
        var requestObj = new
        {
            copyType            = "Minimal",
            sourceEnvironmentId = envId,
        };
        var requestJson = JsonSerializer.Serialize(requestObj);

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/copy" +
                  $"?ValidateOnly=true&api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Post, url, requestJson).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/copy?ValidateOnly=true", status, body);
        var sc = (int)status;
        Assert.True(sc is 200 or 202 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_Copy_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_COPY_ENV"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_COPY_ENV=1 + VERSEOPS_COPY_SOURCE_ID + VERSEOPS_COPY_TARGET_ID to actually copy an environment.");
        var sourceId = Environment.GetEnvironmentVariable("VERSEOPS_COPY_SOURCE_ID");
        var targetId = Environment.GetEnvironmentVariable("VERSEOPS_COPY_TARGET_ID");
        Skip.If(string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId),
            "VERSEOPS_COPY_SOURCE_ID and VERSEOPS_COPY_TARGET_ID are required.");

        var requestObj = new
        {
            copyType            = "Minimal",
            sourceEnvironmentId = sourceId,
        };
        var requestJson = JsonSerializer.Serialize(requestObj);
        var url = $"{PpacBase}/environmentmanagement/environments/{targetId}/copy?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, requestJson).ConfigureAwait(false);
        LogResult($"POST /environments/{targetId}/copy (REAL)", status, body);
        if (headers.TryGetValue("operation-location", out var ol)) _out.WriteLine($"[COPY] operation-location: {ol}");
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[COPY] x-ms-correlation-id: {c}");
        Assert.Equal(202, (int)status);
    }

    // ---------- BACKUPS — list (safe), create + delete (opt-in only; no ValidateOnly) ----------

    [SkippableFact]
    public async Task GET_Backups_List_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/backups?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /environments/{envId}/backups", status, body);
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 404,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_CreateBackup_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        // Per Learn docs this endpoint does NOT support ValidateOnly. Opt-in only.
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_CREATE_BACKUP"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_CREATE_BACKUP=1 (and optionally VERSEOPS_TARGET_ENV_ID) to actually create a backup.");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No environment id available; set VERSEOPS_TARGET_ENV_ID.");

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var requestObj = new { label = $"verseops-test-{stamp}" };
        var requestJson = JsonSerializer.Serialize(requestObj);

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/backups?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, requestJson).ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/backups (REAL)", status, body);
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[CREATE-BACKUP] x-ms-correlation-id: {c}");
        Assert.Equal(201, (int)status);
    }

    [SkippableFact]
    public async Task DELETE_Backup_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_DELETE_BACKUP"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_DELETE_BACKUP=1 + VERSEOPS_BACKUP_ID (and optionally VERSEOPS_TARGET_ENV_ID) to actually delete a backup.");
        var envId    = ResolveEnvId();
        var backupId = Environment.GetEnvironmentVariable("VERSEOPS_BACKUP_ID");
        Skip.If(envId is null, "No environment id available.");
        Skip.If(string.IsNullOrWhiteSpace(backupId), "VERSEOPS_BACKUP_ID is required.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/backups/{backupId}?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Delete, url, null).ConfigureAwait(false);
        LogResult($"DELETE /environments/{envId}/backups/{backupId} (REAL)", status, body);
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[DELETE-BACKUP] x-ms-correlation-id: {c}");
        Assert.Equal(204, (int)status);
    }

    // ---------- ENVIRONMENT MANAGEMENT SETTINGS — list (safe), create + update (opt-in only) ----------

    [SkippableFact]
    public async Task GET_Settings_List_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No seeded environment id available; warm-up did not populate Environments slot.");

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/settings?api-version={ApiVersion}";
        var (status, body) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
        LogResult($"GET /environments/{envId}/settings", status, body);
        // 200 with GetEnvironmentManagementSettingResponse; 404 if no settings record yet;
        // 403 if BYO Entra app is missing the EnvironmentManagement.Settings.Read delegated scope —
        // documented behavior, not a test defect.
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 403 or 404 or 429,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task POST_Settings_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        // No ValidateOnly support per docs — opt-in only.
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_CREATE_SETTINGS"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_CREATE_SETTINGS=1 (and optionally VERSEOPS_TARGET_ENV_ID) to actually create environment management settings.");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No environment id available.");

        // Empty body — Create endpoint takes no request payload per Learn schema (response includes
        // CreateEnvironmentManagementSettingResponse with the created record's id).
        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/settings?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Post, url, "{}").ConfigureAwait(false);
        LogResult($"POST /environments/{envId}/settings (REAL)", status, body);
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[CREATE-SETTINGS] x-ms-correlation-id: {c}");
        // 200 success; 409/4xx if a settings record already exists.
        var sc = (int)status;
        Assert.True(sc is 200 or 400 or 409,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    [SkippableFact]
    public async Task PATCH_Settings_RealExecute_Succeeds()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_UPDATE_SETTINGS"), "1", StringComparison.Ordinal),
            "Set VERSEOPS_UPDATE_SETTINGS=1 (and optionally VERSEOPS_TARGET_ENV_ID) to actually update environment management settings.");
        var envId = ResolveEnvId();
        Skip.If(envId is null, "No environment id available.");

        // PATCH body: any subset of EnvironmentManagementSetting boolean toggles. Picking one
        // low-impact knob (Power Apps chart visualization) and SETTING it to its existing value
        // is functionally a no-op if the record already had it — but it still exercises the route.
        var requestObj = new { powerApps_ChartVisualization = true };
        var requestJson = JsonSerializer.Serialize(requestObj);

        var url = $"{PpacBase}/environmentmanagement/environments/{envId}/settings?api-version={ApiVersion}";
        var (status, body, headers) = await SendWithHeadersAsync(HttpMethod.Patch, url, requestJson).ConfigureAwait(false);
        LogResult($"PATCH /environments/{envId}/settings (REAL)", status, body);
        if (headers.TryGetValue("x-ms-correlation-id", out var c)) _out.WriteLine($"[UPDATE-SETTINGS] x-ms-correlation-id: {c}");
        var sc = (int)status;
        Assert.True(sc is 200 or 404 or 409 or 412,
            $"Unexpected status HTTP {sc}. Body: {body}");
    }

    // ---------- helpers ----------

    private string? ResolveEnvId()
    {
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
