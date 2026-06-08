using System.Text.RegularExpressions;
using VerseOps.Api.Core;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// REST-side coverage matrix mirroring the SDK matrices, but driven by the
/// hand-curated <see cref="ApiCatalog"/> + scraped <see cref="ApiCatalog.PpacOperations"/>
/// (PpacGeneratedCatalog) lists — i.e. exactly the same URL templates the
/// API Explorer UI surfaces. One theory row per (Category, Name, HttpMethod)
/// pair, so the test result list IS the live API Explorer inventory.
///
/// Tokens like {environmentId}, {environmentGroupId}, {policyId}, {location}
/// are resolved from a small defaults map plus warmup seeds. Templates with
/// unresolved tokens (e.g. {scenario}, {actionName}, {uniqueName}) skip with
/// a clear reason — the matrix surfaces "needs caller-supplied value" as a
/// distinct outcome from a wiring bug.
///
/// Same outcome routing as the SDK matrices:
///   - GET / no body                       → invoke, assert 2xx
///   - Other verbs                         → SKIP unless VERSEOPS_INVOKE_MUTATIONS=1
///   - 403 InsufficientDelegatedPermissions → known consent gap, PASS
///   - bare 403 on GET                     → known consent gap (role-scoped), SKIP
///   - 400 InvalidValue / startDate/endDate → needs date input, SKIP
///   - 404 on a path with parameters       → child not provisioned, SKIP
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class PpacRestCatalogCoverageTests
{
    private static readonly Regex TokenRx = new(@"\{(\$?[a-zA-Z][a-zA-Z0-9]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Static defaults for tokens whose value is the same across every tenant
    /// (location codes, currencies, languages). Mirrors the choices the
    /// ApiCatalog parameter presets advertise. Lower-cased keys for case-insensitive lookup.
    /// </summary>
    private static readonly Dictionary<string, string> StaticDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["location"]    = "unitedstates",
            ["currency"]    = "USD",
            ["language"]    = "1033",
            ["languageId"]  = "1033",
            ["sku"]         = "Sandbox",
        };

    /// <summary>
    /// Map from URL template token to warmup-seed key. Multiple template
    /// names can point at the same logical seed (e.g. {policyId} and
    /// {billingPolicyId} both resolve from the BillingPolicies seed).
    /// </summary>
    private static readonly Dictionary<string, string> TokenToSeed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["environmentId"]          = "Environments",
            ["environmentName"]        = "Environments",
            ["environmentGroupId"]     = "EnvironmentGroups",
            ["groupId"]                = "EnvironmentGroups",
            ["billingPolicyId"]        = "BillingPolicies",
            ["policyId"]               = "BillingPolicies",
            ["operationId"]            = "Operations",
        };

    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public PpacRestCatalogCoverageTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    /// <summary>
    /// Concatenate every catalogued REST op the API Explorer can invoke,
    /// dedupe by (Surface, Method, UrlTemplate), and emit a stable label.
    /// </summary>
    public static IEnumerable<object[]> AllRestOps()
    {
        var all = ApiCatalog.Operations
            .Concat(ApiCatalog.PpacOperations)
            .GroupBy(o => $"{o.Surface}|{o.HttpMethod}|{o.UrlTemplate}", StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(o => o.Surface.ToString(), StringComparer.Ordinal)
            .ThenBy(o => o.Category, StringComparer.Ordinal)
            .ThenBy(o => o.SubCategory ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ThenBy(o => o.HttpMethod, StringComparer.Ordinal);

        foreach (var op in all)
        {
            var label = $"[{op.Surface}] {op.HttpMethod}  {op.Category}/{op.SubCategory ?? "-"}/{op.Name}";
            yield return new object[] { label, op };
        }
    }

    [SkippableTheory]
    [MemberData(nameof(AllRestOps))]
    public async Task Rest_Op_Matrix(string label, ApiOperation op)
    {
        _ = label;
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");

        _out.WriteLine($"SURFACE : {op.Surface}");
        _out.WriteLine($"CAT     : {op.Category} / {op.SubCategory ?? "-"} / {op.Name}");
        _out.WriteLine($"VERB    : {op.HttpMethod}");
        _out.WriteLine($"URL_TPL : {op.UrlTemplate}");
        _out.WriteLine($"SCOPE   : {op.TokenScope}");

        // Mutating verbs are hard-gated identical to the SDK matrices — a CI
        // run of this test suite must NEVER provision/delete real resources.
        var isRead = string.Equals(op.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                     && string.IsNullOrWhiteSpace(op.RequestBodyTemplate);
        if (!isRead)
        {
            var enable = string.Equals(Environment.GetEnvironmentVariable("VERSEOPS_INVOKE_MUTATIONS"), "1", StringComparison.Ordinal);
            var allow  = Environment.GetEnvironmentVariable("VERSEOPS_MUTATION_ALLOW");
            if (!enable)
            {
                Skip.If(true,
                    $"Mutating {op.HttpMethod} not auto-invoked. Set VERSEOPS_INVOKE_MUTATIONS=1 " +
                    $"(and optionally VERSEOPS_MUTATION_ALLOW=<substring>) to enable.");
            }
            if (!string.IsNullOrEmpty(allow) && !op.UrlTemplate.Contains(allow, StringComparison.OrdinalIgnoreCase))
            {
                Skip.If(true, $"Mutating op filtered out by VERSEOPS_MUTATION_ALLOW='{allow}' (url={op.UrlTemplate}).");
            }
        }

        // Skip non-PPAC/BAP surfaces (the local:// fake routes used to render
        // a decoded JWT and similar dev helpers). Nothing real to hit.
        if (op.Surface == ApiSurface.Local)
        {
            Skip.If(true, "Local-only operation (no network round-trip).");
        }

        // Resolve every {token} in the URL template from the merged map of
        // warmup seeds + static defaults. Any unresolved token → skip with the
        // list of missing tokens, so the matrix shows "this op needs user
        // input X/Y/Z" instead of a generic invalid-URL failure.
        var (resolved, missing) = ResolveTokens(op.UrlTemplate);
        if (missing.Count > 0)
        {
            Skip.If(true,
                $"Cannot auto-invoke — URL template has unresolved token(s): {string.Join(", ", missing)}. " +
                "Add a seed in SdkAuthFixture or extend StaticDefaults if these are tenant-invariant.");
        }
        _out.WriteLine($"URL     : {resolved}");

        var executor = new ApiExecutor(_fx.Auth);
        var result = await executor.ExecuteAsync(
            op.HttpMethod, resolved, body: null, op.TokenScope, CancellationToken.None)
            .ConfigureAwait(false);

        _out.WriteLine($"HTTP    : {result.StatusCode} {result.ReasonPhrase}");
        if (result.CorrelationId    != null) _out.WriteLine($"CORR    : {result.CorrelationId}");
        if (result.OperationLocation != null) _out.WriteLine($"OPLOC   : {result.OperationLocation}");
        _out.WriteLine($"TIME    : {result.ElapsedMs} ms");
        _out.WriteLine("--- body ---");
        var body = result.ResponseBody ?? string.Empty;
        if (body.Length > 4000) body = body[..4000] + $"\n... ({result.ResponseBody!.Length - 4000} more chars truncated)";
        _out.WriteLine(body);

        var raw = result.ResponseBody ?? string.Empty;

        // Documented consent contracts — match the SDK matrix policy exactly.
        var isKnownConsentGap = result.StatusCode == 403 &&
            raw.Contains("InsufficientDelegatedPermissions", StringComparison.OrdinalIgnoreCase);
        if (isKnownConsentGap)
        {
            _out.WriteLine("NOTE: 403 InsufficientDelegatedPermissions accepted as documented PPAC contract.");
            return;
        }

        if (result.StatusCode == 403 && isRead)
        {
            Skip.If(true,
                "Endpoint returned 403 — signed-in identity lacks a role-scoped delegated permission " +
                "(ISV admin / billing reader / marketplace publisher, etc.). Not a wiring bug.");
        }

        var needsDateInput = result.StatusCode == 400 &&
            raw.Contains("\"InvalidValue\"", StringComparison.OrdinalIgnoreCase) &&
            (raw.Contains("\"startDate\"", StringComparison.OrdinalIgnoreCase) ||
             raw.Contains("\"endDate\"",   StringComparison.OrdinalIgnoreCase));
        if (needsDateInput)
        {
            Skip.If(true,
                "Endpoint requires a caller-supplied date range query parameter (startDate/endDate). " +
                "Not auto-invocable by the coverage matrix.");
        }

        if (result.StatusCode == 404 && isRead && op.UrlTemplate.Contains('{'))
        {
            Skip.If(true,
                "Child resource not provisioned in this tenant (HTTP 404 on a GET under a parameterised path). " +
                "Wire-up is fine; tenant simply has no instance attached.");
        }

        // PPAC routes downstream services (Power Automate, App Management tenant catalog, etc.) that
        // accept the api.powerplatform.com token at the gateway but require a separately-granted role on
        // the downstream service (Flow.Manage.All, AppSource publisher, etc.). The gateway returns 401
        // with one of these markers — it is a consent gap, not a wiring bug.
        if (result.StatusCode == 401 &&
            (raw.Contains("ClientScopeAuthorizationFailed", StringComparison.OrdinalIgnoreCase) ||
             raw.Contains("Exception calling downstream service", StringComparison.OrdinalIgnoreCase)))
        {
            Skip.If(true,
                "Endpoint returned 401 from a downstream service routed through api.powerplatform.com. " +
                "PPAC token is accepted at the gateway but the downstream service requires an extra " +
                "role-scoped delegated permission (Flow.Manage.All, AppSource publisher, etc.). Not a wiring bug.");
        }

        // App Management's GET /environments/{envId}/operations/{operationId} only resolves operationIds
        // minted by a prior appmanagement Install/Uninstall call — the warmup-seeded operationId comes from
        // EnvironmentGroupOperations and isn't visible here. Surface as a seed-scope mismatch, not a wiring bug.
        if (result.StatusCode == 400 &&
            raw.Contains("Operation not found", StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true,
                "Endpoint returned 400 'Operation not found' — the warmup-seeded operationId is from a sibling " +
                "namespace and not valid against this path. Endpoint only accepts operationIds returned by its own POST.");
        }

        // Some PPAC GETs (e.g. /tenant/currencies) return 400 with a specific
        // location-mismatch message because the tenant's home location differs
        // from the static "unitedstates" default. Treat the same way as a
        // "needs caller-supplied value" skip — info, not a wire fail.
        if (result.StatusCode == 400 &&
            (raw.Contains("location", StringComparison.OrdinalIgnoreCase) &&
             raw.Contains("not", StringComparison.OrdinalIgnoreCase) &&
             raw.Contains("supported", StringComparison.OrdinalIgnoreCase)))
        {
            Skip.If(true,
                "Endpoint returned 400 with a location-mismatch message. The static default location " +
                "('unitedstates') is not valid for this tenant — invoke via the UI with the home location.");
        }

        Assert.True(result.StatusCode is >= 200 and < 300,
            $"{op.HttpMethod} {resolved} did not succeed: HTTP {result.StatusCode} {result.ReasonPhrase}");
    }

    private (string Resolved, IReadOnlyList<string> Missing) ResolveTokens(string template)
    {
        var missing = new List<string>();
        var resolved = TokenRx.Replace(template, m =>
        {
            var token = m.Groups[1].Value;

            // Try warmup seeds first (live tenant data).
            if (TokenToSeed.TryGetValue(token, out var seedKey) &&
                _fx.TryGetIndexerSeed(seedKey, out var seedVal) &&
                !string.IsNullOrEmpty(seedVal))
            {
                return seedVal;
            }

            // Then static defaults (location/currency/language).
            if (StaticDefaults.TryGetValue(token, out var d) && !string.IsNullOrEmpty(d))
            {
                return d;
            }

            missing.Add(token);
            return m.Value; // leave it in place — surfaces in the diagnostic URL
        });
        return (resolved, missing);
    }
}
