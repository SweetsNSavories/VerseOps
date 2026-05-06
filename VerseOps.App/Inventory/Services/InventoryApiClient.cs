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
/// One-shot client for the Power Platform Inventory API:
/// <code>
///   POST https://api.powerplatform.com/resourcequery/resources/query
///        ?api-version=2024-10-01
/// </code>
/// A single call returns every Power Platform asset (Canvas / Model-driven /
/// Code apps, Cloud / Agent flows, Copilot Studio agents, ...) across every
/// environment in the tenant. We use the same PPAC scope the rest of the app
/// already holds (<c>https://api.powerplatform.com/.default</c>).
///
/// The body is a polymorphic Kusto-style query against the
/// <c>PowerPlatformResources</c> table. We send a single <c>where type in~ (...)</c>
/// clause to filter to asset types only (envs are pulled separately via
/// the PPAC SDK), and page with <c>SkipToken</c> until <c>resultTruncated == 0</c>.
///
/// Replaces what would otherwise be ~6 per-env API calls × N envs (BAP
/// /apps + /flows, Dataverse /solutions, etc.) — for a 715-env tenant
/// that's ~4,000 fewer round-trips per refresh.
/// </summary>
public sealed class InventoryApiClient
{
    private const string PpBaseUrl  = "https://api.powerplatform.com";
    private const string PpScope    = "https://api.powerplatform.com/.default";
    private const string QueryUrl   = "/resourcequery/resources/query?api-version=2024-10-01";
    private const int    PageSize   = 1000;

    /// <summary>
    /// Resource type filters (KQL <c>in~</c> values, single-quoted as required
    /// by the Inventory API). Environments and environment groups are
    /// intentionally excluded — those come from the PPAC SDK env list which
    /// has richer per-env metadata (region, version, isDefault, ...).
    /// </summary>
    private static readonly string[] AssetTypes =
    {
        "'microsoft.powerapps/canvasapps'",
        "'microsoft.powerapps/modeldrivenapps'",
        "'microsoft.powerapps/codeapps'",
        "'microsoft.powerautomate/cloudflows'",
        "'microsoft.powerautomate/agentflows'",
        "'microsoft.copilotstudio/agents'",
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AuthService _auth;
    private readonly HttpDiagnosticsHandler _diagnostics;

    /// <param name="diagnostics">
    /// Passed in only so we can copy its <see cref="HttpDiagnosticsHandler.ShouldDumpFailure"/>
    /// predicate; we never wire the caller's instance into our pipeline because
    /// a <see cref="DelegatingHandler"/> can have <c>InnerHandler</c> set only once.
    /// </param>
    public InventoryApiClient(AuthService auth, HttpDiagnosticsHandler diagnostics)
    {
        _auth = auth;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Pull every Power Platform asset in the tenant. Pages internally via
    /// <c>SkipToken</c> until the API reports the result set is no longer
    /// truncated. Returns rows tagged with the suffix asset_type
    /// (e.g. <c>canvasapps</c>) — caller persists straight into <c>gov_asset</c>.
    ///
    /// Resilience: per-page transient errors (HttpRequestException, 5xx, 408, 429)
    /// are retried up to 3 times with exponential backoff. If a page still fails
    /// after retries, we stop paging but RETURN whatever we already collected
    /// — losing 60k rows because of one network blip on page 61 is unacceptable.
    /// </summary>
    public async Task<IReadOnlyList<AssetRow>> GetAllAssetsAsync(
        DateTime nowUtc,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Pulling tenant-wide assets from Power Platform Inventory API...");
        var token = await _auth.GetTokenAsync(PpScope, ct).ConfigureAwait(false);

        // Fresh single-use pipeline (see BapCapacityClient for the rationale on
        // not reusing the caller's diagnostics handler instance).
        var ownDiag = new HttpDiagnosticsHandler { ShouldDumpFailure = _diagnostics.ShouldDumpFailure };
        var inner   = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        ownDiag.InnerHandler = inner;
        using var http = new HttpClient(ownDiag, disposeHandler: true)
        {
            BaseAddress = new Uri(PpBaseUrl),
            Timeout = TimeSpan.FromSeconds(150)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var all = new List<AssetRow>(capacity: 4096);
        string skipToken = string.Empty;
        int page = 0;
        int totalReported = -1;
        bool firstPageDumped = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            page++;

            QueryResponse? page1 = null;
            string? lastError = null;

            // Per-page retry loop. Inventory API is fronted by Azure Resource
            // Graph which throttles aggressively on burst paging, and network
            // flakes happen over 100+ POSTs. 3 attempts with backoff is enough
            // to ride through transient blips without hammering on a real outage.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    page1 = await PostQueryAsync(http, skipToken, ct).ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (
                    ex is HttpRequestException
                       or TaskCanceledException
                       or System.IO.IOException
                    && !ct.IsCancellationRequested)
                {
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                    if (attempt == 3) break;
                    var backoffMs = 500 * (1 << (attempt - 1)); // 500ms, 1s
                    progress?.Report($"  Inventory API page {page} attempt {attempt} failed ({lastError}); retrying in {backoffMs}ms...");
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                }
            }

            // Page failed after retries. Don't throw — preserve everything
            // collected so far. The catalog will still get a useful (if
            // incomplete) snapshot, and the next refresh can fill the gap.
            if (page1 is null)
            {
                progress?.Report(
                    $"Inventory API page {page} failed after retries ({lastError ?? "unknown"}). " +
                    $"Persisting partial result of {all.Count} assets " +
                    $"({(totalReported > 0 ? $"{(double)all.Count / totalReported:P0} of {totalReported}" : "no total")}).");
                break;
            }

            // Dump the first page raw to disk so we can verify the response
            // shape matches what the docs describe (200 OK responses are not
            // captured by the diagnostics handler, only failures).
            if (!firstPageDumped)
            {
                try
                {
                    var dumpPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VerseOps", "inventory-api-page1.json");
                    File.WriteAllText(dumpPath, JsonSerializer.Serialize(page1, JsonOpts));
                    progress?.Report($"Inventory API: page1 dump → {dumpPath}");
                }
                catch { /* best effort */ }
                firstPageDumped = true;
            }

            if (page1.Data is { Count: > 0 } items)
            {
                foreach (var node in items)
                {
                    var row = MapAsset(node, nowUtc);
                    if (row is not null) all.Add(row);
                }
            }

            if (totalReported < 0) totalReported = page1.TotalRecords;
            // Report every 10th page to keep the status bar readable on large tenants.
            if (page == 1 || page % 10 == 0)
            {
                progress?.Report(
                    $"Inventory API page {page}: {page1.Count} rows " +
                    $"(running total {all.Count}{(totalReported > 0 ? $" / {totalReported}" : "")}).");
            }

            // Stop conditions: server says we're complete, or no skip token.
            var moreOnServer = page1.ResultTruncated > 0;
            var nextToken    = page1.SkipToken ?? string.Empty;
            if (!moreOnServer || string.IsNullOrEmpty(nextToken)) break;
            skipToken = nextToken;
        }

        progress?.Report($"Inventory API: {all.Count} total assets fetched in {page} page(s).");
        return all;
    }

    /// <summary>
    /// One POST against the inventory query endpoint. Caller owns the retry loop.
    /// </summary>
    private async Task<QueryResponse?> PostQueryAsync(HttpClient http, string skipToken, CancellationToken ct)
    {
        var requestBody = new QueryRequest
        {
            TableName = "PowerPlatformResources",
            Clauses = new ClauseDto[]
            {
                new() { Type = "where", FieldName = "type", Operator = "in~", Values = AssetTypes },
            },
            Options = new OptionsDto
            {
                Top = PageSize,
                Skip = 0,
                SkipToken = skipToken
            }
        };

        using var req  = new HttpRequestMessage(HttpMethod.Post, QueryUrl)
        {
            Content = JsonContent.Create(requestBody, options: JsonOpts)
        };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<QueryResponse>(rawJson, JsonOpts);
    }

    // ------------------------------------------------------------------
    // Map a single ARM-style record into an AssetRow. The Inventory API
    // returns shape: { name, type, location, properties: { displayName,
    // createdAt, createdBy, environmentId, ownerId, lastModifiedAt,
    // lastModifiedBy, isQuarantined, ... } }
    // ------------------------------------------------------------------
    private static AssetRow? MapAsset(ResourceDto node, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(node.Name) || string.IsNullOrEmpty(node.Type)) return null;

        // Strip "microsoft.<vendor>/" prefix; store just the suffix in asset_type.
        var slash = node.Type.IndexOf('/');
        var assetType = slash >= 0 ? node.Type[(slash + 1)..] : node.Type;

        var p = node.Properties;
        return new AssetRow
        {
            AssetId       = node.Name,
            AssetType     = assetType,
            EnvId         = NormalizeEnvId(p?.EnvironmentId),
            DisplayName   = p?.DisplayName,
            OwnerId       = p?.OwnerId,
            CreatedBy     = p?.CreatedBy,
            Region        = node.Location,
            CreatedUtc    = p?.CreatedAt?.UtcDateTime,
            ModifiedUtc   = p?.LastModifiedAt?.UtcDateTime,
            IsQuarantined = p?.IsQuarantined,
            LastSyncedUtc = nowUtc
        };
    }

    /// <summary>
    /// Lower-case the env id so SQLite joins are case-insensitive without
    /// COLLATE NOCASE. PPAC env list returns mixed-case; ARM returns lower.
    /// </summary>
    private static string? NormalizeEnvId(string? envId)
        => string.IsNullOrEmpty(envId) ? null : envId.ToLowerInvariant();

    // ------------------------------------------------------------------
    // Request / response DTOs. Property names are PascalCase to match the
    // server's serializer (the API is case-sensitive — lowercase versions
    // are rejected with 400).
    // ------------------------------------------------------------------
    private sealed class QueryRequest
    {
        [JsonPropertyName("TableName")] public string TableName { get; set; } = "";
        [JsonPropertyName("Clauses")]   public ClauseDto[] Clauses { get; set; } = Array.Empty<ClauseDto>();
        [JsonPropertyName("Options")]   public OptionsDto? Options { get; set; }
    }

    private sealed class ClauseDto
    {
        [JsonPropertyName("$type")]     public string Type { get; set; } = "";
        [JsonPropertyName("FieldName")] public string? FieldName { get; set; }
        [JsonPropertyName("Operator")]  public string? Operator { get; set; }
        [JsonPropertyName("Values")]    public string[]? Values { get; set; }
    }

    private sealed class OptionsDto
    {
        [JsonPropertyName("Top")]       public int Top { get; set; }
        [JsonPropertyName("Skip")]      public int Skip { get; set; }
        [JsonPropertyName("SkipToken")] public string SkipToken { get; set; } = "";
    }

    private sealed class QueryResponse
    {
        [JsonPropertyName("totalRecords")]    public int TotalRecords { get; set; }
        [JsonPropertyName("count")]           public int Count { get; set; }
        [JsonPropertyName("resultTruncated")] public int ResultTruncated { get; set; }
        [JsonPropertyName("skipToken")]       public string? SkipToken { get; set; }
        [JsonPropertyName("data")]            public List<ResourceDto>? Data { get; set; }
    }

    private sealed class ResourceDto
    {
        [JsonPropertyName("name")]       public string? Name { get; set; }
        [JsonPropertyName("type")]       public string? Type { get; set; }
        [JsonPropertyName("location")]   public string? Location { get; set; }
        [JsonPropertyName("tenantId")]   public string? TenantId { get; set; }
        [JsonPropertyName("properties")] public AssetPropsDto? Properties { get; set; }
    }

    private sealed class AssetPropsDto
    {
        [JsonPropertyName("displayName")]    public string? DisplayName { get; set; }
        [JsonPropertyName("environmentId")]  public string? EnvironmentId { get; set; }
        [JsonPropertyName("ownerId")]        public string? OwnerId { get; set; }
        [JsonPropertyName("createdBy")]      public string? CreatedBy { get; set; }
        [JsonPropertyName("createdAt")]      public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("lastModifiedAt")] public DateTimeOffset? LastModifiedAt { get; set; }
        [JsonPropertyName("lastModifiedBy")] public string? LastModifiedBy { get; set; }
        [JsonPropertyName("isQuarantined")]  public bool? IsQuarantined { get; set; }
    }
}
