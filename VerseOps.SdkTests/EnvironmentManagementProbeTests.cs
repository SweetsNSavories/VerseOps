using Microsoft.Kiota.Abstractions;
using VerseOps.App.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Probes every reflected Environmentmanagement.* SDK operation against the
/// live tenant under the signed-in identity. Two theories:
///
///   - <see cref="Read_Op_Succeeds"/>: every GET op, including those with an
///     env-id / group-id indexer. Indexer values are auto-seeded by the
///     fixture's warmup pass; if no seed exists for a required slot the row
///     is Skipped rather than Failed, so missing data is visible without
///     polluting the failure count.
///
///   - <see cref="Mutating_Op_Listed"/>: every POST / PUT / PATCH / DELETE
///     op. Always Skipped — these would actually create / modify / delete
///     real resources in the signed-in tenant, so they need targeted tests
///     that seed inputs and verify cleanup, not blanket invocation. The row
///     exists so the pass/skip matrix surfaces the full op inventory at a
///     glance.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentManagementProbeTests
{
    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentManagementProbeTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    public static IEnumerable<object[]> AllReadOps()
        => EnumerateOps("GET");

    public static IEnumerable<object[]> AllMutatingOps()
        => SdkCatalog.Operations
            .Where(o => o.HttpMethod != "GET"
                        && o.Path.Count > 0
                        && string.Equals(o.Path[0].PropertyName, "Environmentmanagement", StringComparison.Ordinal))
            .OrderBy(o => o.PathText)
            .ThenBy(o => o.HttpMethod)
            .Select(o => new object[] { o.PathText + "  " + o.HttpMethod, o });

    private static IEnumerable<object[]> EnumerateOps(string verb)
        => SdkCatalog.Operations
            .Where(o => o.HttpMethod == verb
                        && o.Path.Count > 0
                        && string.Equals(o.Path[0].PropertyName, "Environmentmanagement", StringComparison.Ordinal)
                        && o.BodyType == null)
            .OrderBy(o => o.PathText)
            .Select(o => new object[] { o.PathText, o });

    [SkippableTheory]
    [MemberData(nameof(AllReadOps))]
    public async Task Read_Op_Succeeds(string pathText, SdkOp op)
    {
        _ = pathText;
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");

        // Seed indexer values from the fixture's warmup corpus. Slot keys are
        // the parent collection name (e.g. "Environments") per SdkStep.SlotKey.
        var indexerValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in op.IndexerSlots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_fx.TryGetIndexerSeed(slot, out var id))
            {
                Skip.If(true, $"No seed available for indexer slot '{slot}'. Add a warmup call for it in SdkAuthFixture.");
            }
            indexerValues[slot] = id;
        }

        var executor = new SdkExecutor(_fx.Auth);
        var result = await executor.ExecuteAsync(
            op,
            indexerValues: indexerValues,
            jsonBody: null,
            ct: CancellationToken.None).ConfigureAwait(false);

        _out.WriteLine($"PATH  : {op.PathText}");
        _out.WriteLine($"VERB  : {op.HttpMethod}");
        _out.WriteLine($"INDEX : {(indexerValues.Count == 0 ? "(none)" : string.Join(", ", indexerValues.Select(kv => $"{kv.Key}={kv.Value}")))}");
        _out.WriteLine($"STATUS: {result.StatusText}");
        _out.WriteLine($"TIME  : {result.ElapsedMs} ms");
        _out.WriteLine("---");
        var body = result.Body ?? string.Empty;
        if (body.Length > 4000) body = body[..4000] + $"\n... ({result.Body!.Length - 4000} more chars truncated)";
        _out.WriteLine(body);

        // 403 InsufficientDelegatedPermissions is a documented PPAC contract for BYO
        // Entra apps that lack the required delegated scope (e.g. /settings needs
        // EnvironmentManagement.Settings.Read). Treat it as a known-good outcome
        // so the smoke matrix surfaces wiring bugs instead of consent gaps.
        var isKnownConsentGap = result.HttpStatusCode == 403 &&
            (result.Body ?? string.Empty).Contains("InsufficientDelegatedPermissions", StringComparison.OrdinalIgnoreCase);
        if (isKnownConsentGap)
        {
            _out.WriteLine("NOTE: 403 InsufficientDelegatedPermissions accepted as documented PPAC contract.");
            return;
        }

        Assert.True(result.Success, $"{op.PathText} did not succeed: {result.StatusText}");
    }

    [SkippableTheory]
    [MemberData(nameof(AllMutatingOps))]
    public void Mutating_Op_Listed(string label, SdkOp op)
    {
        // Always-skipped row. Surfaces the op in the test matrix so we can
        // see what mutating coverage looks like at a glance, without ever
        // accidentally invoking a destructive verb from a blanket test run.
        _out.WriteLine($"PATH  : {op.PathText}");
        _out.WriteLine($"VERB  : {op.HttpMethod}");
        _out.WriteLine($"BODY  : {op.BodyType?.FullName ?? "(none)"}");
        Skip.If(true, $"{op.HttpMethod} not auto-invoked from blanket suite (would mutate live tenant). Write a targeted test.");
        _ = label;
    }
}
