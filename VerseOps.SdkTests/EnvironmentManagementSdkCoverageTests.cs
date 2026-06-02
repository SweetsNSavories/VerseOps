using System.Text.Json;
using VerseOps.App.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Per-op coverage matrix for the entire <c>Environmentmanagement</c> surface
/// reflected from Microsoft.PowerPlatform.Management. One theory row per
/// (PathText, HttpMethod) pair, so the test result list is the live SDK inventory.
///
/// Read-side rows actually invoke the op (with warmup-seeded indexer values when
/// required) and assert success. Mutating rows (POST/PUT/PATCH/DELETE) are
/// hard-gated behind per-op env vars — defaults SKIP rather than fire, because
/// hitting these would Recover/Restore/ModifySku/Copy/AddEnvironment/etc. on a
/// real org under the signed-in identity.
///
/// To actually invoke a mutating op, set:
///   VERSEOPS_INVOKE_MUTATIONS=1                  (master switch)
///   VERSEOPS_MUTATION_ALLOW=&lt;substring&gt;          (filter: only paths containing this substring fire)
///
/// Example, fire only EnvironmentGroups CRUD (the pre-authorised throwaway path):
///   $env:VERSEOPS_INVOKE_MUTATIONS = '1'
///   $env:VERSEOPS_MUTATION_ALLOW   = 'EnvironmentGroups'
///
/// The dedicated <see cref="EnvironmentGroupCrudTests"/> still owns the round-trip
/// proof; this matrix exists so coverage gaps in the SDK are visible at a glance.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentManagementSdkCoverageTests
{
    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentManagementSdkCoverageTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    public static IEnumerable<object[]> AllEnvironmentManagementOps()
        => SdkCatalog.Operations
            .Where(o => o.Path.Count > 0
                        && string.Equals(o.Path[0].PropertyName, "Environmentmanagement", StringComparison.Ordinal))
            .OrderBy(o => o.PathText)
            .ThenBy(o => o.HttpMethod)
            .Select(o => new object[] { $"{o.HttpMethod}  {o.PathText}", o });

    [SkippableTheory]
    [MemberData(nameof(AllEnvironmentManagementOps))]
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
                Skip.If(true, $"No warmup seed for indexer slot '{slot}'. Add a seed source in SdkAuthFixture.");
            }
            indexerValues[slot] = id;
        }
        _out.WriteLine($"INDEX   : {(indexerValues.Count == 0 ? "(none)" : string.Join(", ", indexerValues.Select(kv => $"{kv.Key}={kv.Value}")))}");

        // --- Routing rules ---
        var isRead = op.HttpMethod == "GET" && op.BodyType == null;
        if (isRead)
        {
            await InvokeAndAssertAsync(op, indexerValues, jsonBody: null);
            return;
        }

        // Mutating op — only fire under explicit opt-in.
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

        // Default body for opted-in mutating ops: minimal stub. Specific ops should
        // be invoked from purpose-built tests (see EnvironmentGroupCrudTests).
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

        // 403 InsufficientDelegatedPermissions is a documented PPAC contract for BYO
        // Entra apps that lack the required delegated scope (e.g. /settings needs
        // EnvironmentManagement.Settings.Read). Treat it as a known-good outcome
        // so the coverage matrix surfaces wiring bugs instead of consent gaps.
        var isKnownConsentGap = result.HttpStatusCode == 403 &&
            (result.Body ?? string.Empty).Contains("InsufficientDelegatedPermissions", StringComparison.OrdinalIgnoreCase);
        if (isKnownConsentGap)
        {
            _out.WriteLine("NOTE: 403 InsufficientDelegatedPermissions accepted as documented PPAC contract.");
            return;
        }

        Assert.True(result.Success, $"{op.HttpMethod} {op.PathText} did not succeed: {result.StatusText}");
    }

    /// <summary>
    /// Builds a one-line JSON body for opted-in mutating ops. Per-op overrides
    /// can be added here as we learn each endpoint's minimum-shape requirements.
    /// For unknown shapes we emit <c>{}</c> and let the SDK / server validation
    /// surface the missing fields in the response body.
    /// </summary>
    private static string BuildMinimalBody(SdkOp op)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        // EnvironmentGroups POST — the only mutating op we have proven shape for.
        if (op.PathText == "ServiceClient.Environmentmanagement.EnvironmentGroups" && op.HttpMethod == "POST")
            return JsonSerializer.Serialize(new
            {
                displayName = $"verseops-matrix-{stamp}",
                description = "Created by VerseOps.SdkTests coverage matrix (opt-in).",
            });
        return "{}";
    }
}
