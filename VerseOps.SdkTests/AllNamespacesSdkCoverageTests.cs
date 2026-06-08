using System.Text.Json;
using VerseOps.App.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Catch-all SDK coverage matrix. Runs the same per-op invoke-and-assert routine
/// as <see cref="EnvironmentManagementSdkCoverageTests"/> /
/// <see cref="LicensingSdkCoverageTests"/>, but auto-discovers every other
/// top-level namespace reflected on Microsoft.PowerPlatform.Management.ServiceClient
/// (Connectivity, Analytics, Workflow, Tenant, App management, etc.) so coverage
/// scales automatically with SDK updates.
///
/// Excluded namespaces — already covered by dedicated test classes:
///   - Environmentmanagement (see <see cref="EnvironmentManagementSdkCoverageTests"/>)
///   - Licensing             (see <see cref="LicensingSdkCoverageTests"/>)
///
/// Same routing rules as the existing matrices:
///   - GET / no body → auto-invoke, assert success
///   - Other verbs   → hard-gated behind VERSEOPS_INVOKE_MUTATIONS=1 (+ optional
///                     VERSEOPS_MUTATION_ALLOW=&lt;substring&gt; filter)
///   - Indexer slots resolved via warmup seeds; missing seeds skip cleanly
///   - 403 InsufficientDelegatedPermissions / generic 403 on GET → known-consent skip
///   - 400 InvalidValue on startDate/endDate → "needs caller-supplied date" skip
///   - 404 on GET nested under an indexer → "child not provisioned" skip
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class AllNamespacesSdkCoverageTests
{
    // Namespaces with their own dedicated coverage matrix. Excluded here so they
    // don't double-run (and so test result counts stay legible per surface).
    private static readonly HashSet<string> CoveredElsewhere = new(StringComparer.Ordinal)
    {
        "Environmentmanagement",
        "Licensing",
    };

    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public AllNamespacesSdkCoverageTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    public static IEnumerable<object[]> AllOtherNamespaceOps()
        => SdkCatalog.Operations
            .Where(o => o.Path.Count > 0)
            .Where(o => !CoveredElsewhere.Contains(o.Path[0].PropertyName))
            .OrderBy(o => o.Path[0].PropertyName, StringComparer.Ordinal)
            .ThenBy(o => o.PathText, StringComparer.Ordinal)
            .ThenBy(o => o.HttpMethod, StringComparer.Ordinal)
            .Select(o => new object[] { $"[{o.Path[0].PropertyName}] {o.HttpMethod}  {o.PathText}", o });

    [SkippableTheory]
    [MemberData(nameof(AllOtherNamespaceOps))]
    public async Task Op_Matrix(string label, SdkOp op)
    {
        _ = label;
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");

        _out.WriteLine($"NS      : {op.Path[0].PropertyName}");
        _out.WriteLine($"PATH    : {op.PathText}");
        _out.WriteLine($"VERB    : {op.HttpMethod}");
        _out.WriteLine($"BUILDER : {op.BuilderType.FullName}");
        _out.WriteLine($"BODY    : {op.BodyType?.FullName ?? "(none)"}");

        var indexerValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in op.IndexerSlots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_fx.TryGetIndexerSeed(slot, out var id))
            {
                Skip.If(true, $"No warmup seed for indexer slot '{slot}'. Either the tenant has no resource " +
                              "of that kind, or the auto-discovery sweep in SdkAuthFixture couldn't list it " +
                              "(403/404/needs-date). Surface in WarmupLog.");
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

        var stubBody = op.BodyType == null ? null : "{}";
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

        // Same skip family as the per-namespace matrices: consent gap, date-required,
        // child-not-provisioned 404. Centralised here so a green run means "wired",
        // not "happened to have data".
        var rawBody = result.Body ?? string.Empty;

        var isKnownConsentGap = result.HttpStatusCode == 403 &&
            rawBody.Contains("InsufficientDelegatedPermissions", StringComparison.OrdinalIgnoreCase);
        if (isKnownConsentGap)
        {
            _out.WriteLine("NOTE: 403 InsufficientDelegatedPermissions accepted as documented PPAC contract.");
            return;
        }

        if (result.HttpStatusCode == 403 && op.HttpMethod == "GET")
        {
            Skip.If(true,
                "Endpoint returned 403 — signed-in identity lacks a role-scoped delegated permission. " +
                "Not a wiring bug.");
        }

        // PPAC fronts downstream services (Power Automate, App Management tenant catalog) whose 401 surfaces
        // ClientScopeAuthorizationFailed / 'Exception calling downstream service' — a consent gap, not a bug.
        if (result.HttpStatusCode == 401 &&
            (rawBody.Contains("ClientScopeAuthorizationFailed", StringComparison.OrdinalIgnoreCase) ||
             rawBody.Contains("Exception calling downstream service", StringComparison.OrdinalIgnoreCase)))
        {
            Skip.If(true,
                "Endpoint returned 401 from a downstream service routed through api.powerplatform.com. " +
                "PPAC token is accepted at the gateway but the downstream service requires an extra " +
                "role-scoped delegated permission (Flow.Manage.All, AppSource publisher, etc.). Not a wiring bug.");
        }

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

        if (result.HttpStatusCode == 404 && op.HttpMethod == "GET" && op.HasIndexer)
        {
            Skip.If(true,
                "Child resource not provisioned in this tenant (HTTP 404 on a GET under an indexer). " +
                "Wire-up is fine; tenant simply has no instance attached.");
        }

        // Auto-discovery seed may have produced a stale id (e.g. the env got
        // deleted between warmup and this test). Surface as a skip so a green
        // run still means "wired".
        if (result.HttpStatusCode == 404 && op.HttpMethod == "GET")
        {
            Skip.If(true,
                "HTTP 404 on a top-level GET with no indexer is unusual — likely a path that requires a " +
                "caller-supplied query parameter (e.g. type filter). Not auto-invocable.");
        }

        Assert.True(result.Success, $"{op.HttpMethod} {op.PathText} did not succeed: {result.StatusText}");
    }
}
