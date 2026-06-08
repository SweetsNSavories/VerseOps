using System.IO;
using System.Text.Json;
using VerseOps.App.Auth;
using VerseOps.App.Configuration;
using VerseOps.App.Sdk;
using Xunit;

namespace VerseOps.SdkTests;

/// <summary>
/// One-time interactive sign-in shared by every test in the collection.
/// xUnit guarantees a single instance for all classes that opt into
/// <see cref="SdkAuthCollection"/>, so the browser pops at most once per
/// <c>dotnet test</c> invocation. Token caching is in-memory only (MSAL's
/// default) — we never persist a token to disk from the test rig.
///
/// After sign-in the fixture also runs a small "corpus warmup" that calls
/// the well-known list endpoints (Environments, EnvironmentGroups) once and
/// caches the first id returned for each. Indexed theory rows pull these via
/// <see cref="TryGetIndexerSeed"/> so we don't need to hand-edit a fixed env
/// id into the test code.
/// </summary>
public sealed class SdkAuthFixture : IAsyncLifetime
{
    public const string PpacScope = "https://api.powerplatform.com/.default";

    public AuthService Auth { get; } = new();
    public bool SignedIn { get; private set; }
    public string? SignInError { get; private set; }

    /// <summary>Diagnostic log of every warmup call (path → status → seed-id).</summary>
    public List<string> WarmupLog { get; } = new();

    // Map of indexer slot key (e.g. "Environments", "EnvironmentGroups") to a
    // live id discovered at warmup time. Case-insensitive so callers don't
    // have to remember whether SdkOp surfaces the slot in PascalCase or not.
    private readonly Dictionary<string, string> _seeds = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetIndexerSeed(string slotKey, out string id)
    {
        if (_seeds.TryGetValue(slotKey, out var v) && !string.IsNullOrEmpty(v))
        {
            id = v;
            return true;
        }
        id = string.Empty;
        return false;
    }

    public async Task InitializeAsync()
    {
        // Pick up tenant/client id from the same %LOCALAPPDATA%\VerseOps\appsettings.json
        // the WPF app reads, so BYO-app-registration users get the right values for free.
        AppSettings.LoadFromDisk();
        Auth.TenantId       = AppSettings.Current.TenantId;
        Auth.PublicClientId = AppSettings.Current.PublicClientId;
        Auth.UseBroker      = false; // system browser — no parent-window-handle plumbing needed.

        try
        {
            // 1) Always try the persisted MSAL cache first. With the on-disk DPAPI cache
            //    wired up in AuthService.RegisterPersistentCache, the user signs in once
            //    via the WPF app (or any prior test run) and every subsequent process
            //    — including this fixture — gets the access token silently.
            var silent = await Auth.TryGetTokenSilentAsync(PpacScope, CancellationToken.None).ConfigureAwait(false);
            if (silent != null)
            {
                SignedIn = true;
            }
            else
            {
                // 2) Hard gate: when running headless / in CI / when the developer
                //    explicitly said "do not block on a browser prompt", skip cleanly
                //    instead of leaving the test host hanging on a sign-in window
                //    that nobody is going to answer.
                var noninteractive = string.Equals(
                    Environment.GetEnvironmentVariable("VERSEOPS_AUTH_NONINTERACTIVE"),
                    "1", StringComparison.Ordinal);
                if (noninteractive)
                {
                    SignInError = "VERSEOPS_AUTH_NONINTERACTIVE=1: no cached MSAL account; refusing to open a browser. Sign in via the WPF app first.";
                    SignedIn = false;
                    return;
                }

                // 3) Last resort: device-code sign-in. The test rig is a console
                //    host with no parent window — system-browser interactive
                //    flows have no reliable way to bring the browser to the
                //    foreground here, so we print a code the user types into
                //    https://microsoft.com/devicelogin from any browser/device.
                //    Code is written to BOTH stderr (visible if tests stream
                //    live) AND a fixed file under %LOCALAPPDATA%\VerseOps so
                //    `dotnet test` runs that capture output to a log still let
                //    the user see the code.
                var codeFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VerseOps", "devicecode.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(codeFile)!);

                await Auth.SignInDeviceCodeAsync(PpacScope, dc =>
                {
                    var msg =
                        "================ VerseOps test harness sign-in ================\n" +
                        $"Open:  {dc.VerificationUrl}\n" +
                        $"Code:  {dc.UserCode}\n" +
                        $"Expires: {dc.ExpiresOn:O}\n" +
                        "===============================================================";
                    Console.Error.WriteLine(msg);
                    try { File.WriteAllText(codeFile, msg); } catch { /* best-effort */ }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                SignedIn = true;
            }
        }
        catch (Exception ex)
        {
            // Don't fail InitializeAsync — that would abort the whole class with
            // a single confusing error. Tests check SignedIn and Skip cleanly.
            SignInError = ex.Message;
            SignedIn = false;
            return;
        }

        await WarmupAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WarmupAsync(CancellationToken ct)
    {
        var executor = new SdkExecutor(Auth);
        // Find the two top-level list builders we want to use as seed sources.
        // PathText match keeps this resilient to SDK builder renames.
        var seedSources = new (string PathText, string SeedKey)[]
        {
            ("ServiceClient.Environmentmanagement.Environments",      "Environments"),
            ("ServiceClient.Environmentmanagement.EnvironmentGroups", "EnvironmentGroups"),
        };

        foreach (var (pathText, seedKey) in seedSources)
        {
            var op = SdkCatalog.Operations.FirstOrDefault(o =>
                o.HttpMethod == "GET" && o.PathText == pathText && !o.HasIndexer && o.BodyType == null);
            if (op is null)
            {
                WarmupLog.Add($"WARMUP MISS  {pathText}: op not found in catalog (SDK shape changed?)");
                continue;
            }

            var result = await executor.ExecuteAsync(op,
                indexerValues: new Dictionary<string, string>(),
                jsonBody: null,
                ct: ct).ConfigureAwait(false);

            if (!result.Success)
            {
                WarmupLog.Add($"WARMUP FAIL  {pathText}: {result.StatusText} :: {Trunc(result.Body)}");
                continue;
            }

            // The executor renders the Kiota response with System.Text.Json default
            // options, so property names are PascalCase. Both EnvironmentList and
            // EnvironmentGroupResponseWithOdataContinuation expose a `Value` array
            // and each item has a `Name` (env id) or `Id` field.
            var seedId = ExtractFirstId(result.Body);
            if (string.IsNullOrEmpty(seedId))
            {
                WarmupLog.Add($"WARMUP EMPTY {pathText}: response had no items to seed from");
                continue;
            }

            _seeds[seedKey] = seedId;
            WarmupLog.Add($"WARMUP OK    {pathText} → {seedKey}={seedId}");
        }

        // ---- Phase 2: indexer-dependent seeds (need a prior seed to call them) ----
        // EnvironmentGroupOperations is a top-level list — same pattern as phase 1
        // but produces a seed for the indexer slot "EnvironmentGroupOperations".
        await TrySeedFromList(executor,
            pathText: "ServiceClient.Environmentmanagement.EnvironmentGroupOperations",
            seedKey:  "EnvironmentGroupOperations",
            indexerValues: new Dictionary<string, string>(),
            ct: ct).ConfigureAwait(false);

        // Operations under an environment — needs the env-id we seeded above.
        if (_seeds.TryGetValue("Environments", out var envIdForOps))
        {
            await TrySeedFromList(executor,
                pathText: "ServiceClient.Environmentmanagement.Environments.Item[environmentId].Operations",
                seedKey:  "Operations",
                indexerValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Environments"] = envIdForOps
                },
                ct: ct).ConfigureAwait(false);
        }

        // Licensing.BillingPolicies — top-level list under the Licensing namespace.
        // Empty on most tenants; LicensingSdkCoverageTests skips the indexed rows
        // cleanly when no seed is produced.
        await TrySeedFromList(executor,
            pathText: "ServiceClient.Licensing.BillingPolicies",
            seedKey:  "BillingPolicies",
            indexerValues: new Dictionary<string, string>(),
            ct: ct).ConfigureAwait(false);

        // ---- Phase 3: auto-discovery sweep ----
        // Walk every top-level GET list endpoint that takes no indexer and no body
        // (i.e. ServiceClient.<Namespace>.<Collection> with no required input) and
        // seed _seeds[<Collection>] with the first id. Without this, any namespace
        // we haven't curated above (Connectivity, Workflow, Analytics, etc.) would
        // skip every indexed sub-path with "no warmup seed", which masks real
        // wiring bugs as data gaps. Best-effort: per-call failures are logged and
        // skipped so a single 403/404 doesn't abort warmup.
        var seenSeedKeys = new HashSet<string>(_seeds.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var op in SdkCatalog.Operations)
        {
            if (op.HttpMethod != "GET") continue;
            if (op.BodyType != null) continue;
            if (op.HasIndexer) continue;
            if (op.Path.Count < 2) continue;
            // Seed key is the collection step name (last segment of the path).
            var seedKey = op.Path[^1].PropertyName;
            if (string.IsNullOrEmpty(seedKey)) continue;
            if (seenSeedKeys.Contains(seedKey)) continue;
            seenSeedKeys.Add(seedKey);
            await TrySeedFromList(executor, op.PathText, seedKey,
                indexerValues: new Dictionary<string, string>(),
                ct: ct).ConfigureAwait(false);
        }

        // ---- Phase 4: nested-indexer auto-discovery ----
        // For every Environments[envId].<Collection> GET list, seed the inner
        // collection name. Lets indexed children (e.g. .Settings, .Users)
        // resolve their inner indexer slots without per-namespace curation.
        if (_seeds.TryGetValue("Environments", out var envIdForNested))
        {
            foreach (var op in SdkCatalog.Operations)
            {
                if (op.HttpMethod != "GET") continue;
                if (op.BodyType != null) continue;
                if (op.Path.Count < 4) continue;
                // Path shape: ServiceClient.<Ns>.Environments.Item[environmentId].<Coll>
                if (op.Path[2].PropertyName != "Environments") continue;
                if (!op.Path[3].IsIndexer) continue;
                // Last step must be a non-indexer collection name.
                var tail = op.Path[^1];
                if (tail.IsIndexer) continue;
                var seedKey = tail.PropertyName;
                if (string.IsNullOrEmpty(seedKey)) continue;
                if (seenSeedKeys.Contains(seedKey)) continue;
                seenSeedKeys.Add(seedKey);
                await TrySeedFromList(executor, op.PathText, seedKey,
                    indexerValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Environments"] = envIdForNested
                    },
                    ct: ct).ConfigureAwait(false);
            }
        }
    }

    private async Task TrySeedFromList(
        SdkExecutor executor,
        string pathText,
        string seedKey,
        IReadOnlyDictionary<string, string> indexerValues,
        CancellationToken ct)
    {
        var op = SdkCatalog.Operations.FirstOrDefault(o =>
            o.HttpMethod == "GET" && o.PathText == pathText && o.BodyType == null);
        if (op is null)
        {
            WarmupLog.Add($"WARMUP MISS  {pathText}: op not found in catalog");
            return;
        }
        var result = await executor.ExecuteAsync(op, indexerValues, jsonBody: null, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            WarmupLog.Add($"WARMUP FAIL  {pathText}: {result.StatusText} :: {Trunc(result.Body)}");
            return;
        }
        var seedId = ExtractFirstId(result.Body);
        if (string.IsNullOrEmpty(seedId))
        {
            WarmupLog.Add($"WARMUP EMPTY {pathText}: response had no items to seed from");
            return;
        }
        _seeds[seedKey] = seedId;
        WarmupLog.Add($"WARMUP OK    {pathText} → {seedKey}={seedId}");
    }

    private static string? ExtractFirstId(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            // PPAC list endpoints use one of several wrapper keys: `Value` (EnvironmentList,
            // EnvironmentGroupResponseWithOdataContinuation), `Collection`
            // (OperationExecutionResultPagedCollection), or `Items`.
            JsonElement arr = default;
            foreach (var name in new[] { "Value", "Collection", "Items" })
            {
                if (doc.RootElement.TryGetProperty(name, out var candidate) &&
                    candidate.ValueKind == JsonValueKind.Array)
                {
                    arr = candidate;
                    break;
                }
            }
            if (arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in arr.EnumerateArray())
            {
                // Priority matters: PPAC operation objects have BOTH `Name` (a type label
                // like "Promote") AND `OperationId` (the GUID we actually need). Prefer
                // the explicit *Id keys before the generic Name fallback.
                foreach (var idKey in new[] { "OperationId", "EnvironmentGroupOperationId", "BillingPolicyId", "Id", "Name" })
                {
                    if (item.TryGetProperty(idKey, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
        }
        catch { /* warmup is best-effort */ }
        return null;
    }

    private static string Trunc(string? s, int max = 400)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s!.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(Name)]
public sealed class SdkAuthCollection : ICollectionFixture<SdkAuthFixture>
{
    public const string Name = "SdkAuth";
}
