using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using VerseOps.App.Auth;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// One-shot client for the BAP DLP (Data Loss Prevention) policy list, the
/// admin-center governance surface that classifies connectors into
/// Business / Non-Business / Blocked buckets and binds those rule sets to
/// environments.
/// <code>
///   GET https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01
/// </code>
/// The v2 envelope returns flat policy objects (no nested "properties" wrapper)
/// with <c>connectorGroups</c>, <c>environmentType</c> and <c>environments[]</c>.
/// We deliberately call only the LIST endpoint — per-policy detail (connector
/// list, custom URL patterns) is paged independently and only needed when the
/// admin drills into a specific policy.
/// Mirrors the BapCapacityClient pattern: fresh per-call HttpClient, fresh
/// HttpDiagnosticsHandler (single-assignment InnerHandler), token from the
/// shared <see cref="AuthService"/>.
/// </summary>
public sealed class BapDlpClient
{
    private const string BapBaseUrl = "https://api.bap.microsoft.com";
    private const string BapScope   = "https://service.powerapps.com/.default";
    private const string ListPoliciesUrl =
        "/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AuthService _auth;
    private readonly HttpDiagnosticsHandler _diagnostics;

    public BapDlpClient(AuthService auth, HttpDiagnosticsHandler diagnostics)
    {
        _auth = auth;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Pull every DLP policy visible to the signed-in admin. Empty list on
    /// no-policies-yet tenants. Throws on auth failure or non-2xx so callers
    /// can surface the body via the standard error panel.
    /// </summary>
    public async Task<IReadOnlyList<DlpPolicyDto>> ListPoliciesAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Pulling DLP policies from BAP (one call)...");
        var token = await _auth.GetTokenAsync(BapScope, ct).ConfigureAwait(false);

        // Same single-assignment-inner-handler dance as BapCapacityClient.
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

        using var resp = await http.GetAsync(ListPoliciesUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Dump the raw payload (best-effort) for offline inspection — same
        // pattern as bap-capacity-last.json.
        var rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var dumpPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerseOps", "bap-dlp-last.json");
            File.WriteAllText(dumpPath, rawJson);
            progress?.Report($"BAP DLP raw response dumped to {dumpPath} ({rawJson.Length} bytes).");
        }
        catch { /* best effort */ }

        var payload = JsonSerializer.Deserialize<EnvelopeDto>(rawJson, JsonOpts);
        return (IReadOnlyList<DlpPolicyDto>?)payload?.Value ?? Array.Empty<DlpPolicyDto>();
    }

    // ------------------------------------------------------------------
    // BAP v2/policies response DTOs (only the fields we surface).
    // ------------------------------------------------------------------
    public sealed class EnvelopeDto
    {
        [JsonPropertyName("value")] public List<DlpPolicyDto>? Value { get; set; }
    }

    /// <summary>
    /// Flat DLP policy. The v2 endpoint does NOT wrap fields under "properties"
    /// (unlike the older Microsoft.BusinessAppPlatform/scopes/admin/apiPolicies
    /// envelope). Use this directly.
    /// </summary>
    public sealed class DlpPolicyDto
    {
        [JsonPropertyName("name")]              public string? Name { get; set; }            // policy GUID
        [JsonPropertyName("displayName")]       public string? DisplayName { get; set; }
        [JsonPropertyName("createdBy")]         public PrincipalDto? CreatedBy { get; set; }
        [JsonPropertyName("createdTime")]       public DateTime? CreatedTime { get; set; }
        [JsonPropertyName("lastModifiedBy")]    public PrincipalDto? LastModifiedBy { get; set; }
        [JsonPropertyName("lastModifiedTime")]  public DateTime? LastModifiedTime { get; set; }

        /// <summary>"AllEnvironments" | "OnlyEnvironments" | "ExceptEnvironments".</summary>
        [JsonPropertyName("environmentType")]   public string? EnvironmentType { get; set; }

        /// <summary>Populated only when <see cref="EnvironmentType"/> is OnlyEnvironments / ExceptEnvironments.</summary>
        [JsonPropertyName("environments")]      public List<EnvRefDto>? Environments { get; set; }

        /// <summary>"Confidential" | "General" | "Blocked" — what unclassified connectors fall back to.</summary>
        [JsonPropertyName("defaultConnectorsClassification")] public string? DefaultClassification { get; set; }

        /// <summary>Buckets — each holds the connector list at one classification.</summary>
        [JsonPropertyName("connectorGroups")]   public List<ConnectorGroupDto>? ConnectorGroups { get; set; }
    }

    public sealed class PrincipalDto
    {
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("objectId")]    public string? ObjectId { get; set; }
    }

    public sealed class EnvRefDto
    {
        [JsonPropertyName("id")]   public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    public sealed class ConnectorGroupDto
    {
        /// <summary>"Confidential" (Business) | "General" (Non-Business) | "Blocked".</summary>
        [JsonPropertyName("classification")] public string? Classification { get; set; }
        [JsonPropertyName("connectors")]     public List<ConnectorDto>? Connectors { get; set; }
    }

    public sealed class ConnectorDto
    {
        [JsonPropertyName("id")]   public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}
