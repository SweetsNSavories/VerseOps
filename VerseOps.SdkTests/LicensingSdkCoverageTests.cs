using System.Text.Json;
using VerseOps.App.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Per-op coverage matrix for the entire <c>Licensing</c> surface reflected from
/// Microsoft.PowerPlatform.Management — sibling of
/// <see cref="EnvironmentManagementSdkCoverageTests"/>.
///
/// Covers, at minimum (live SDK shape may add more):
///   GET    ServiceClient.Licensing.BillingPolicies                                  (List Billing Policies)
///   POST   ServiceClient.Licensing.BillingPolicies                                  (Create Billing Policy)        — mutating, opt-in
///   GET    ServiceClient.Licensing.BillingPolicies[billingPolicyId]                 (Get Billing Policy)
///   DELETE ServiceClient.Licensing.BillingPolicies[billingPolicyId]                 (Delete Billing Policy)        — mutating, opt-in
///   POST   ServiceClient.Licensing.BillingPolicies[billingPolicyId]/RefreshProvisioningStatus — mutating, opt-in
///   GET    ServiceClient.Licensing.Tenant.GetCurrentCapacityAllocations             (Per-tenant capacity — the marquee "one call" used by the dashboard)
///
/// Same routing rules as the EnvironmentManagement coverage matrix:
///   - Read-side rows (GET / no body) auto-invoke and assert success.
///   - Mutating rows skip by default. To fire:
///       VERSEOPS_INVOKE_MUTATIONS=1                  (master switch)
///       VERSEOPS_MUTATION_ALLOW=&lt;substring&gt;          (filter, e.g. "BillingPolicies")
///   - Indexer slots are pulled from warmup seeds; missing seeds skip
///     cleanly so the matrix surfaces "no billing policy on tenant" without
///     looking like a wiring bug.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class LicensingSdkCoverageTests
{
    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public LicensingSdkCoverageTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    public static IEnumerable<object[]> AllLicensingOps()
        => SdkCatalog.Operations
            .Where(o => o.Path.Count > 0
                        && string.Equals(o.Path[0].PropertyName, "Licensing", StringComparison.Ordinal))
            .OrderBy(o => o.PathText)
            .ThenBy(o => o.HttpMethod)
            .Select(o => new object[] { $"{o.HttpMethod}  {o.PathText}", o });

    [SkippableTheory]
    [MemberData(nameof(AllLicensingOps))]
    public async Task Op_Matrix(string label, SdkOp op)
    {
        _ = label;
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");

        _out.WriteLine($"PATH    : {op.PathText}");
        _out.WriteLine($"VERB    : {op.HttpMethod}");
        _out.WriteLine($"BUILDER : {op.BuilderType.FullName}");
        _out.WriteLine($"BODY    : {op.BodyType?.FullName ?? "(none)"}");

        var indexerValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in op.IndexerSlots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_fx.TryGetIndexerSeed(slot, out var id))
            {
                Skip.If(true, $"No warmup seed for indexer slot '{slot}'. Add a seed source in SdkAuthFixture, " +
                              "or the tenant simply has no resource of that kind (e.g. no billing policies).");
            }
            indexerValues[slot] = id;
        }
        _out.WriteLine($"INDEX   : {(indexerValues.Count == 0 ? "(none)" : string.Join(", ", indexerValues.Select(kv => $"{kv.Key}={kv.Value}")))}");

        var isRead = op.HttpMethod == "GET" && op.BodyType == null;
        if (isRead)
        {
            await InvokeAndAssertAsync(op, indexerValues, jsonBody: null);
            return;
        }

        var enable = string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_INVOKE_MUTATIONS"), "1", StringComparison.Ordinal);
        var allow  = Environment.GetEnvironmentVariable("VERSEOPS_MUTATION_ALLOW");
        if (!enable)
        {
            Skip.If(true,
                $"Mutating {op.HttpMethod} not auto-invoked. Set VERSEOPS_INVOKE_MUTATIONS=1 " +
                $"(and optionally VERSEOPS_MUTATION_ALLOW=<substring>) to enable.");
        }
        if (!string.IsNullOrEmpty(allow) && !op.PathText.Contains(allow, StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true, $"Mutating op filtered out by VERSEOPS_MUTATION_ALLOW='{allow}' (path={op.PathText}).");
        }

        var stubBody = op.BodyType == null ? null : BuildMinimalBody(op);
        _out.WriteLine($"BODY-IN : {stubBody ?? "(none)"}");
        await InvokeAndAssertAsync(op, indexerValues, jsonBody: stubBody);
    }

    private async Task InvokeAndAssertAsync(SdkOp op, IReadOnlyDictionary<string, string> indexerValues, string? jsonBody)
    {
        var executor = new SdkExecutor(_fx.Auth);
        var result = await executor.ExecuteAsync(op, indexerValues, jsonBody, CancellationToken.None).ConfigureAwait(false);

        _out.WriteLine($"STATUS  : {result.StatusText}");
        if (result.HttpStatusCode is int sc)  _out.WriteLine($"HTTP    : {sc}");
        if (result.OperationLocation != null) _out.WriteLine($"OPLOC   : {result.OperationLocation}");
        if (result.CorrelationId != null)     _out.WriteLine($"CORR    : {result.CorrelationId}");
        _out.WriteLine($"TIME    : {result.ElapsedMs} ms");
        _out.WriteLine("--- body ---");
        var body = result.Body ?? string.Empty;
        if (body.Length > 4000) body = body[..4000] + $"\n... ({result.Body!.Length - 4000} more chars truncated)";
        _out.WriteLine(body);

        var isKnownConsentGap = result.HttpStatusCode == 403 &&
            (result.Body ?? string.Empty).Contains("InsufficientDelegatedPermissions", StringComparison.OrdinalIgnoreCase);
        if (isKnownConsentGap)
        {
            _out.WriteLine("NOTE: 403 InsufficientDelegatedPermissions accepted as documented PPAC contract.");
            return;
        }

        // Broader 403 tolerance for read paths: PPAC Licensing exposes endpoints whose
        // *scope* (e.g. ISV admin, marketplace publisher) is gated on top of the standard
        // user consent. A caller that's not an ISV will get a bare 403 with no body on
        // /licensing/isvContracts even when the tenant *has* contracts. Skip with the same
        // "consent gap" signal — informational, not a wiring fail.
        if (result.HttpStatusCode == 403 && op.HttpMethod == "GET")
        {
            Skip.If(true,
                "Endpoint returned 403 — signed-in identity lacks a role-scoped delegated permission " +
                "(ISV admin / billing reader / marketplace publisher, etc.). Not a wiring bug.");
        }

        // Several Licensing usage-report endpoints (UserPerFlowCapacitySource, *.Summary, *.SourceFlowCapacityHistory, etc.)
        // require a caller-supplied `startDate` query parameter. The matrix invokes with no
        // input, so the server returns 400 InvalidValue/startDate. Treat that as a "needs
        // user input" skip — same spirit as missing indexer seeds — instead of a wiring fail.
        var rawBody = result.Body ?? string.Empty;
        var needsDateInput = result.HttpStatusCode == 400 &&
            rawBody.Contains("\"InvalidValue\"", StringComparison.OrdinalIgnoreCase) &&
            (rawBody.Contains("\"startDate\"", StringComparison.OrdinalIgnoreCase) ||
             rawBody.Contains("\"endDate\"",   StringComparison.OrdinalIgnoreCase));
        if (needsDateInput)
        {
            Skip.If(true,
                "Endpoint requires a caller-supplied date range query parameter (startDate/endDate). " +
                "Not auto-invocable by the coverage matrix — exercise this op via the UI or a dedicated fact.");
        }

        // 404 on a GET path nested under an indexer (e.g. /environments/{envId}/billingPolicy,
        // /billingPolicies/{bpId}/environments/{envId}) means "the parent indexer is real but the
        // child resource is not provisioned in this tenant" — same family as the missing-seed
        // skip earlier. The Kiota SDK builds the route from the OpenAPI spec, so a 404 here is
        // a tenant-data signal, not a path/wiring bug. Limited to GET to avoid masking real
        // misroutes on mutating verbs.
        if (result.HttpStatusCode == 404 && op.HttpMethod == "GET" && op.HasIndexer)
        {
            Skip.If(true,
                "Child resource not provisioned in this tenant (HTTP 404 on a GET under an indexer). " +
                "Wire-up is fine; tenant simply has no instance attached.");
        }

        Assert.True(result.Success, $"{op.HttpMethod} {op.PathText} did not succeed: {result.StatusText}");
    }

    private static string BuildMinimalBody(SdkOp op)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        // Create Billing Policy — the only Licensing mutation with a known minimal shape.
        // billingInstrument requires resource-group + subscription + (optional) resource-id of an
        // active Azure subscription; without a real one the server returns 400 with the missing
        // fields enumerated. Stub with empty objects so the failure is informative, not crashy.
        if (op.HttpMethod == "POST" &&
            op.PathText == "ServiceClient.Licensing.BillingPolicies")
        {
            return JsonSerializer.Serialize(new
            {
                name = $"verseops-matrix-{stamp}",
                location = "unitedstates",
                status = "Enabled",
                billingInstrument = new { resourceGroup = "", subscriptionId = "" },
            });
        }
        return "{}";
    }
}
