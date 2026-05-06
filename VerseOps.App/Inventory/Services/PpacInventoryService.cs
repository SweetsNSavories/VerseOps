using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.PowerPlatform.Management;
using VerseOps.App.Auth;
using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// Pulls environments + capacity from the Power Platform Management SDK
/// (api.powerplatform.com) and persists to the local SQLite catalog.
/// No BAP routes; SDK-only. Per-env capacity is fetched from
/// Licensing.Environments[envId].Allocations.
/// </summary>
public sealed class PpacInventoryService : IInventoryService
{
    private const string PpacBaseUrl = "https://api.powerplatform.com";
    private const string PpacScope   = "https://api.powerplatform.com/.default";
    private const string ApiVersion  = "2022-03-01-preview";

    private readonly AuthService _auth;
    private readonly SqliteCatalog _catalog;
    private readonly HttpDiagnosticsHandler _diagnostics = new()
    {
        // 404 on /licensing/environments/{id}/allocations means "this env has
        // no licensing allocations" — completely benign. Don't dump a full
        // 30-line block per env to the trace; just keep the one-liner.
        // (Kept for safety; we no longer call /allocations from the inventory
        // refresh, but other tools in the app may still hit it.)
        ShouldDumpFailure = static (req, res) =>
        {
            if ((int)res.StatusCode != 404) return true;
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            return !path.Contains("/licensing/environments/", StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith("/allocations", StringComparison.OrdinalIgnoreCase);
        }
    };

    public PpacInventoryService(AuthService auth, SqliteCatalog catalog)
    {
        _auth = auth;
        _catalog = catalog;
        _catalog.EnsureCreated();
    }

    /// <summary>Path to the trace log written by every HTTP call.</summary>
    public string TraceLogPath => _diagnostics.LogPath;

    /// <summary>Snapshot of the last non-2xx response captured by the diagnostics handler.</summary>
    public FailureSnapshot? LastFailure => HttpDiagnosticsHandler.LastFailure;

    public IReadOnlyList<EnvironmentRow> Load()
        => _catalog.ReadAllEnvironments();

    public IReadOnlyList<TenantCapacityEntry> LoadTenantCapacity()
        => _catalog.ReadAllTenantCapacity();

    public IReadOnlyList<AssetRow> LoadAssets()
        => _catalog.ReadAllAssets();

    public DateTime? LastSyncedUtc()
        => _catalog.LastRefreshedUtc();

    /// <summary>
    /// Per-env Dataverse drill-down. The row itself isn't mutated here —
    /// the caller (view-model) owns the property assignments + threading.
    /// </summary>
    public async Task<DataverseEnvClient.EnvDetails> LoadEnvironmentDetailsAsync(
        EnvironmentRow env,
        IReadOnlyList<AssetRow> envAssets,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(env.InstanceUrl))
            throw new InvalidOperationException(
                "This environment has no Dataverse instance URL — no database to query (it may be a Teams or Developer env without Dataverse, or PPAC hasn't reported the URL yet).");

        var client = new DataverseEnvClient(_auth, _diagnostics);
        return await client.LoadAllAsync(env.EnvId, env.InstanceUrl, envAssets, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Quick "is the signed-in user a member of this env?" check used by the
    /// "Only my environments" toggle. Returns <c>false</c> for envs with no
    /// Dataverse (nothing to be a member of); otherwise delegates to
    /// <see cref="DataverseEnvClient.CheckCurrentUserMembershipAsync"/>.
    /// </summary>
    public async Task<bool?> CheckCurrentUserMembershipAsync(
        EnvironmentRow env,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(env.InstanceUrl)) return false;
        var client = new DataverseEnvClient(_auth, _diagnostics);
        return await client.CheckCurrentUserMembershipAsync(env.InstanceUrl, ct).ConfigureAwait(false);
    }

    // Cached single Graph license fetch — second call returns the same instance
    // so the dashboard's tenant-wide rollup tile + per-env user enrichment
    // share one paged trip across the tenant directory.
    private GraphLicenseClient? _graphLicenses;

    public async Task<GraphLicenseClient> LoadGraphLicensesAsync(CancellationToken ct = default)
    {
        if (_graphLicenses is not null) return _graphLicenses;
        var client = new GraphLicenseClient(_auth);
        await client.LoadAsync(ct).ConfigureAwait(false);
        _graphLicenses = client;
        return client;
    }

    public async Task<HashSet<string>> CheckSecurityGroupMembershipAsync(
        IEnumerable<string> groupIds,
        CancellationToken ct = default)
    {
        // Re-uses the cached license client so we don't bring up a second
        // HttpClient just for one POST. If LoadGraphLicensesAsync hasn't been
        // called yet, that's fine — the client just constructs and goes.
        var client = _graphLicenses ?? new GraphLicenseClient(_auth);
        return await client.CheckSecurityGroupMembershipAsync(groupIds, ct).ConfigureAwait(false);
    }

    public async Task<RefreshResult> RefreshAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _diagnostics.ResetLog();
        progress?.Report($"Trace log: {_diagnostics.LogPath}");
        progress?.Report("Acquiring PPAC token...");

        var sc = await BuildClientAsync(ct).ConfigureAwait(false);

        progress?.Report("Listing environments...");
        var envList = await sc.Environmentmanagement.Environments.GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var envRows = new List<EnvironmentRow>();
        var capRows = new List<CapacityEntry>();

        var rawEnvs = ExtractList(envList);
        progress?.Report($"Mapping {rawEnvs.Count} environments...");

        foreach (var raw in rawEnvs)
        {
            ct.ThrowIfCancellationRequested();
            var row = MapEnvironment(raw, now);
            if (row is null) continue;
            envRows.Add(row);
        }

        // Per-env capacity. PPAC SDK has no per-env capacity surface today
        // (Licensing.Environments[id].Allocations returns license currencies
        // — AI / AppPass / PowerAutomate units — not Database/File/Log GB).
        // The single official source for that is the BAP route the
        // Get-AdminPowerAppEnvironment -Capacity cmdlet wraps:
        //
        //   GET /providers/Microsoft.BusinessAppPlatform/scopes/admin/environments
        //       ?$expand=properties/capacity&api-version=2020-10-01
        //
        // One HTTP call returns all envs + their capacity in one payload.
        // This is the *only* BAP route the inventory dashboard uses; everything
        // else is on api.powerplatform.com via the PPAC SDK. When MS surfaces
        // per-env capacity on the new API, BapCapacityClient is the swap point.
        progress?.Report("Fetching per-env capacity from BAP (one call)...");
        int envsWithCapacity = 0;
        try
        {
            var bap = new BapCapacityClient(_auth, _diagnostics);
            var byEnv = await bap.GetCapacityByEnvAsync(now, progress, ct).ConfigureAwait(false);

            foreach (var row in envRows)
            {
                if (byEnv.TryGetValue(row.EnvId, out var rows) && rows.Count > 0)
                {
                    capRows.AddRange(rows);
                    envsWithCapacity++;
                }
            }
            progress?.Report($"BAP capacity: {envsWithCapacity}/{envRows.Count} envs reported capacity rows.");
        }
        catch (Exception ex)
        {
            progress?.Report($"  (BAP capacity failed: {ex.GetType().Name}: {ex.Message}) — falling back to env list only.");
        }

        // Tenant-wide storage / API capacity (Database / File / Log / FinOpsDatabase
        // / ApiCallCount / etc.). PPAC reports storage in MB; UI converts to GB.
        progress?.Report("Fetching tenant-wide capacity totals...");
        var tenantRows = new List<TenantCapacityEntry>();
        try
        {
            var tenantCap = await sc.Licensing.TenantCapacity.GetAsync(cancellationToken: ct).ConfigureAwait(false);
            foreach (var entry in MapTenantCapacity(tenantCap, now))
                tenantRows.Add(entry);
        }
        catch (Exception ex)
        {
            progress?.Report($"  (tenant capacity failed: {ex.GetType().Name}: {ex.Message})");
        }

        // Tenant-wide asset catalog (Canvas / Model-driven / Code apps,
        // Cloud / Agent flows, Copilot Studio agents) via the new Power
        // Platform Inventory API:
        //   POST https://api.powerplatform.com/resourcequery/resources/query
        //        ?api-version=2024-10-01
        // ONE call replaces what would otherwise be 6+ per-env round-trips
        // (apps + flows + agents) × N envs. The API is GA as of 2024-10
        // and uses the same PPAC scope we already hold.
        progress?.Report("Fetching tenant-wide assets (Power Platform Inventory API)...");
        var assetRows = new List<AssetRow>();
        try
        {
            var inv = new InventoryApiClient(_auth, _diagnostics);
            var pulled = await inv.GetAllAssetsAsync(now, progress, ct).ConfigureAwait(false);
            assetRows.AddRange(pulled);
            progress?.Report($"Inventory API: {assetRows.Count} assets pulled tenant-wide.");
        }
        catch (Exception ex)
        {
            progress?.Report($"  (Inventory API failed: {ex.GetType().Name}: {ex.Message}) — continuing without asset catalog.");
        }

        progress?.Report(
            $"Persisting {envRows.Count} environments + {capRows.Count} capacity rows + " +
            $"{tenantRows.Count} tenant capacity rows + {assetRows.Count} assets...");
        _catalog.ReplaceAll(envRows, capRows, tenantRows, assetRows);

        sw.Stop();
        progress?.Report($"Done in {sw.Elapsed.TotalSeconds:0.0}s.");
        return new RefreshResult(envRows.Count, capRows.Count, assetRows.Count, sw.Elapsed);
    }

    // ------------------------------------------------------------------
    // ServiceClient construction (mirrors VerseOps.SdkProbe + SdkExecutor).
    // ------------------------------------------------------------------
    private async Task<ServiceClient> BuildClientAsync(CancellationToken ct)
    {
        // Re-acquire the token per refresh so MSAL silent-cache expiry handles itself.
        var token = await _auth.GetTokenAsync(PpacScope, ct).ConfigureAwait(false);
        var tokenProvider = new StaticTokenAccessProvider(token);
        var authProv = new BaseBearerTokenAuthenticationProvider(tokenProvider);

        var handlers = KiotaClientFactory.CreateDefaultHandlers();
        // Use the SDK-provided ApiVersionHandler (same as VerseOps.SdkProbe). Kiota
        // pre-adds "?api-version=" with an empty value when QueryParameters.ApiVersion
        // isn't set; the SDK handler replaces the empty value, our local one only added
        // when missing -> 400 ApiVersionInvalid.
        handlers.Insert(0, new Microsoft.PowerPlatform.Management.ApiVersionHandler(ApiVersion));
        handlers.Insert(0, _diagnostics); // outermost so it sees the final response

        var http = KiotaClientFactory.Create(handlers);
        http.Timeout = TimeSpan.FromSeconds(150);

        var adapter = new HttpClientRequestAdapter(authProv, httpClient: http) { BaseUrl = PpacBaseUrl };
        return new ServiceClient(adapter);
    }

    // ------------------------------------------------------------------
    // Reflection-based mapping. The Kiota-generated model classes ship as
    // closed types under Microsoft.PowerPlatform.Management.Models; rather
    // than hard-binding to every property name (which can drift across SDK
    // versions), we read by name with PascalCase keys verified against the
    // probe sample data in probe-results.json.
    // ------------------------------------------------------------------
    private static IReadOnlyList<object> ExtractList(object? response)
    {
        if (response is null) return Array.Empty<object>();
        var t = response.GetType();
        var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProp?.GetValue(response) is System.Collections.IEnumerable e)
        {
            var list = new List<object>();
            foreach (var x in e) if (x is not null) list.Add(x);
            return list;
        }
        return Array.Empty<object>();
    }

    private static EnvironmentRow? MapEnvironment(object env, DateTime now)
    {
        var t = env.GetType();
        string? id = Get<string>(env, t, "Id");
        if (string.IsNullOrEmpty(id)) return null;

        // Type field can be a nullable enum (e.g. Production / Default / Sandbox).
        string? typeStr = ToStringOrNull(Get<object>(env, t, "Type"));

        // ProtectionLevel ("Standard" = Managed Environment, "Basic" = unmanaged).
        // Surfaced from the typed property when present, with a fallback to the
        // AdditionalData bag for SDK versions where it lives there.
        string? protection = ToStringOrNull(Get<object>(env, t, "ProtectionLevel"));
        if (string.IsNullOrEmpty(protection))
            protection = ReadAdditionalString(env, t, "protectionLevel");
        bool isManaged = string.Equals(protection, "Standard", StringComparison.OrdinalIgnoreCase);

        // securityGroupId only appears in the AdditionalData bag — there's no
        // typed surface for it on the SDK Environment object yet. Empty/missing
        // means the env has no security group attached (open to whole tenant).
        string? secGroupId = ReadAdditionalString(env, t, "securityGroupId");
        if (string.IsNullOrEmpty(secGroupId) || secGroupId == "00000000-0000-0000-0000-000000000000")
            secGroupId = null;

        return new EnvironmentRow
        {
            EnvId = id,
            DisplayName = Get<string>(env, t, "DisplayName"),
            Sku = typeStr,
            Region = Get<string>(env, t, "AzureRegion") ?? Get<string>(env, t, "Geo"),
            ProvisioningState = ToStringOrNull(Get<object>(env, t, "State")),
            Version = Get<string>(env, t, "Version"),
            InstanceUrl = Get<string>(env, t, "Url"),
            IsDefault = string.Equals(typeStr, "Default", StringComparison.OrdinalIgnoreCase),
            CreatedUtc = ToUtcOrNull(Get<object>(env, t, "CreatedDateTime")),
            LastSyncedUtc = now,
            SecurityGroupId = secGroupId,
            IsManagedEnvironment = isManaged
        };
    }

    /// <summary>
    /// Reads a string from a Kiota <c>AdditionalData</c> bag (the catch-all
    /// dictionary on every generated model for fields not yet bound to a
    /// typed property). Returns null when the property doesn't exist or the
    /// AdditionalData bag is missing.
    /// </summary>
    private static string? ReadAdditionalString(object instance, Type t, string key)
    {
        var addProp = t.GetProperty("AdditionalData", BindingFlags.Public | BindingFlags.Instance);
        if (addProp?.GetValue(instance) is not System.Collections.IDictionary dict) return null;
        if (!dict.Contains(key)) return null;
        return dict[key]?.ToString();
    }

    /// <summary>
    /// Map TenantCapacityDetailsModel.TenantCapacities (List&lt;TenantCapacityAndConsumptionModel&gt;)
    /// into our flattened TenantCapacityEntry rows. Reflection-based so we don't
    /// hard-bind to the SDK types and survive minor version drift.
    /// </summary>
    private static IEnumerable<TenantCapacityEntry> MapTenantCapacity(object? root, DateTime now)
    {
        if (root is null) yield break;
        var rt = root.GetType();
        var listProp = rt.GetProperty("TenantCapacities", BindingFlags.Public | BindingFlags.Instance);
        if (listProp?.GetValue(root) is not System.Collections.IEnumerable items) yield break;

        foreach (var item in items)
        {
            if (item is null) continue;
            var it = item.GetType();

            // Consumption is a nested object with a numeric "TotalConsumption" field.
            double? consumed = null;
            var cons = Get<object>(item, it, "Consumption");
            if (cons is not null)
            {
                var ct = cons.GetType();
                consumed = AsDouble(Get<object>(cons, ct, "TotalConsumption"))
                           ?? AsDouble(Get<object>(cons, ct, "Consumed"))
                           ?? AsDouble(Get<object>(cons, ct, "UnitsConsumed"));
            }

            yield return new TenantCapacityEntry
            {
                CapacityType  = ToStringOrNull(Get<object>(item, it, "CapacityType")) ?? "Unknown",
                Units         = ToStringOrNull(Get<object>(item, it, "CapacityUnits")),
                MaxCapacity   = AsDouble(Get<object>(item, it, "MaxCapacity")),
                TotalCapacity = AsDouble(Get<object>(item, it, "TotalCapacity")),
                Consumed      = consumed,
                Status        = ToStringOrNull(Get<object>(item, it, "Status")),
                LastSyncedUtc = now
            };
        }
    }

    private static T? Get<T>(object instance, Type t, string propName)
    {
        var prop = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return default;
        var v = prop.GetValue(instance);
        if (v is null) return default;
        if (v is T direct) return direct;
        try { return (T)Convert.ChangeType(v, typeof(T)); }
        catch { return default; }
    }

    private static string? ToStringOrNull(object? v)
        => v?.ToString() is { Length: > 0 } s ? s : null;

    private static DateTime? ToUtcOrNull(object? v)
    {
        return v switch
        {
            null => null,
            DateTime dt => dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt.ToUniversalTime(),
            DateTimeOffset dto => dto.UtcDateTime,
            string s when DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static double? AsDouble(object? v) => v switch
    {
        null => null,
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var x) => x,
        _ => null
    };

    // ------------------------------------------------------------------
    // Kiota plumbing.
    // ------------------------------------------------------------------
    private sealed class StaticTokenAccessProvider : IAccessTokenProvider
    {
        private readonly string _token;
        public StaticTokenAccessProvider(string token) { _token = token; }
        public AllowedHostsValidator AllowedHostsValidator { get; } = new();
        public Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }
}
