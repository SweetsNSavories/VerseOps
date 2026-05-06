using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VerseOps.App.Auth;
using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// One-shot client for the BAP capacity-by-environment endpoint that the
/// official PowerShell admin module wraps with <c>Get-AdminPowerAppEnvironment -Capacity</c>:
/// <code>
///   GET https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments
///       ?$expand=properties/capacity&amp;api-version=2020-10-01
/// </code>
/// Returns every environment together with its capacity rows (Database / File /
/// Log / etc.) in a single HTTP call. This is the ONLY BAP route the inventory
/// dashboard uses; everything else is on api.powerplatform.com via the PPAC SDK.
/// We keep this isolated here so when Microsoft adds a per-env capacity surface
/// to the new Power Platform API, swapping it out is a single-file change.
/// </summary>
public sealed class BapCapacityClient
{
    private const string BapBaseUrl = "https://api.bap.microsoft.com";
    private const string BapScope   = "https://service.powerapps.com/.default";
    // The official Microsoft.PowerApps.Administration.PowerShell module uses a
    // dot ("properties.capacity") rather than a slash here. The slash form is
    // silently accepted by BAP but the capacity block is omitted from the
    // response. Use the dot form to actually get capacity rows back.
    private const string ListWithCapacityUrl =
        "/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments" +
        "?api-version=2020-10-01&$expand=properties.capacity";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AuthService _auth;
    private readonly HttpDiagnosticsHandler _diagnostics;

    /// <summary>
    /// The diagnostics handler is used purely to copy its <see cref="HttpDiagnosticsHandler.ShouldDumpFailure"/>
    /// predicate onto a fresh per-client instance. We never attach the caller's
    /// shared handler instance into our own pipeline — a DelegatingHandler can
    /// have its <c>InnerHandler</c> set only once, so reusing it across HttpClients
    /// triggers "This instance has already started one or more requests."
    /// </summary>
    public BapCapacityClient(AuthService auth, HttpDiagnosticsHandler diagnostics)
    {
        _auth = auth;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Pull the env list with capacity. Returns a map of envId -&gt; capacity rows.
    /// Empty map on transport failure (caller can choose to continue without
    /// per-env capacity). Throws on auth failure or non-2xx so callers can
    /// surface it via the standard error panel.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CapacityEntry>>> GetCapacityByEnvAsync(
        DateTime nowUtc,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Pulling per-env capacity from BAP (one call)...");
        var token = await _auth.GetTokenAsync(BapScope, ct).ConfigureAwait(false);

        // Build a fresh, single-use pipeline. We deliberately construct a NEW
        // HttpDiagnosticsHandler rather than reuse the caller's instance: a
        // DelegatingHandler can only be attached once, and the PPAC client
        // already owns the shared one.
        var ownDiag = new HttpDiagnosticsHandler { ShouldDumpFailure = _diagnostics.ShouldDumpFailure };
        var inner   = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        ownDiag.InnerHandler = inner;
        using var http = new HttpClient(ownDiag, disposeHandler: true)
        {
            BaseAddress = new Uri(BapBaseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.GetAsync(ListWithCapacityUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // TEMP: dump the raw BAP response to disk so we can inspect the
        // actual JSON shape (200 OK responses are not captured by the
        // diagnostics handler, only failures).
        var rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var dumpPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerseOps", "bap-capacity-last.json");
            File.WriteAllText(dumpPath, rawJson);
            progress?.Report($"BAP raw response dumped to {dumpPath} ({rawJson.Length} bytes).");
        }
        catch { /* best effort */ }

        var payload = JsonSerializer.Deserialize<EnvelopeDto>(rawJson, JsonOpts);
        var byEnv = new Dictionary<string, IReadOnlyList<CapacityEntry>>(StringComparer.OrdinalIgnoreCase);
        if (payload?.Value is null)
        {
            progress?.Report("BAP returned 0 envs.");
            return byEnv;
        }

        int withCap = 0;
        foreach (var env in payload.Value)
        {
            if (string.IsNullOrEmpty(env.Name)) continue;
            var caps = env.Properties?.Capacity;
            if (caps is null || caps.Count == 0) continue;

            var rows = new List<CapacityEntry>(caps.Count);
            foreach (var c in caps)
            {
                if (string.IsNullOrEmpty(c.CapacityType)) continue;
                rows.Add(new CapacityEntry
                {
                    EnvId         = env.Name,
                    CapacityType  = c.CapacityType,
                    Actual        = c.ActualConsumption,
                    Rated         = c.RatedConsumption,
                    Total         = c.RatedConsumption, // BAP only returns actual+rated; total alias for backwards compat
                    LastSyncedUtc = nowUtc
                });
            }
            byEnv[env.Name] = rows;
            withCap++;
        }

        progress?.Report($"BAP returned {payload.Value.Count} envs ({withCap} with capacity).");
        return byEnv;
    }

    // ------------------------------------------------------------------
    // BAP response DTOs (only the fields we map). Power Platform sends the
    // capacity block as: { capacityType: "Database", actualConsumption: 12.5,
    // ratedConsumption: 100, capacityUnit: "MB", updatedOn: "..." }.
    // ------------------------------------------------------------------
    private sealed class EnvelopeDto
    {
        [JsonPropertyName("value")]
        public List<EnvDto>? Value { get; set; }
    }

    private sealed class EnvDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("id")]   public string? Id   { get; set; }
        [JsonPropertyName("properties")] public PropsDto? Properties { get; set; }
    }

    private sealed class PropsDto
    {
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("capacity")]    public List<CapacityDto>? Capacity { get; set; }
    }

    private sealed class CapacityDto
    {
        [JsonPropertyName("capacityType")]      public string? CapacityType      { get; set; }
        [JsonPropertyName("actualConsumption")] public double? ActualConsumption { get; set; }
        [JsonPropertyName("ratedConsumption")]  public double? RatedConsumption  { get; set; }
        [JsonPropertyName("capacityUnit")]      public string? CapacityUnit      { get; set; }
        [JsonPropertyName("updatedOn")]         public DateTimeOffset? UpdatedOn { get; set; }
    }
}
