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

    public IReadOnlyList<TenantCurrencyReportEntry> LoadTenantCurrencyReports()
        => _catalog.ReadAllCurrencyReports();

    public IReadOnlyList<BillingPolicyRow> LoadBillingPolicies()
        => _catalog.ReadAllBillingPolicies();

    public IReadOnlyList<AssetRow> LoadAssets()
        => _catalog.ReadAllAssets();

    public DateTime? LastSyncedUtc()
        => _catalog.LastRefreshedUtc();

    /// <summary>
    /// Per-env Dataverse drill-down. Cache-first: the FIRST expansion of a
    /// row hits the network, persists the result into <c>gov_env_details</c>,
    /// and returns it. Every subsequent expansion in this process — and
    /// every expansion in future app launches — hydrates synchronously from
    /// SQLite (no HTTP at all). The per-env "Refresh" button calls this with
    /// <paramref name="forceRefresh"/> = <c>true</c> to bypass the cache.
    /// <para>
    /// The row itself isn't mutated here — the caller (view-model) owns the
    /// property assignments + threading. Per-asset enrichments (Status,
    /// IsPremium, DlpStatus, SolutionName, IsManaged) ARE stamped onto the
    /// AssetRow instances in <paramref name="envAssets"/> on both code
    /// paths so the bound grids light up immediately.
    /// </para>
    /// <para>
    /// On a force-refresh the cached row is deleted BEFORE the network
    /// fetch starts, so a failed re-fetch leaves the user with "no cache"
    /// rather than a stale snapshot — clean failure mode.
    /// </para>
    /// Also kicks the tenant DLP policy fetch (cached after first call) so
    /// the canvas-app enrichment can stamp <see cref="AssetRow.DlpStatus"/>
    /// in the same Dataverse round-trip. The DLP fetch is best-effort: if
    /// the user lacks the policy.read permission, canvas apps still get
    /// status + premium classification stamped, just no DLP badge.
    /// </summary>
    public Task<DataverseEnvClient.EnvDetails> LoadEnvironmentDetailsAsync(
        EnvironmentRow env,
        IReadOnlyList<AssetRow> envAssets,
        CancellationToken ct = default)
        => LoadEnvironmentDetailsAsync(env, envAssets, forceRefresh: false, ct);

    public async Task<DataverseEnvClient.EnvDetails> LoadEnvironmentDetailsAsync(
        EnvironmentRow env,
        IReadOnlyList<AssetRow> envAssets,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(env.InstanceUrl))
            throw new InvalidOperationException(
                "This environment has no Dataverse instance URL — no database to query (it may be a Teams or Developer env without Dataverse, or PPAC hasn't reported the URL yet).");

        // Cache-first path. Snapshot stamps enrichments back onto the live
        // AssetRow instances in envAssets before returning, so flat-view
        // grids see Status / IsPremium / DlpStatus / SolutionName /
        // IsManaged populated without us having to re-hit Dataverse.
        if (!forceRefresh)
        {
            var cached = _catalog.ReadEnvDetails(env.EnvId);
            if (cached.HasValue)
            {
                var snap = EnvDetailsSnapshot.Deserialize(cached.Value.payloadJson);
                if (snap is not null)
                {
                    // Self-heal stale snapshots written by older builds that
                    // didn't synthesize the "(unmatched)" SolutionGroup when
                    // Dataverse returned zero visible solutions. If the env
                    // has assets but the cached snapshot stored 0 solutions,
                    // the hydrated grouped view would render an empty
                    // SOLUTIONS section forever — drop the row and fall
                    // through to a live fetch so the orphan-group logic
                    // (LoadSolutionsAsync) gets a chance to populate.
                    bool isStale = snap.Solutions.Count == 0 && envAssets.Count > 0;
                    if (!isStale)
                    {
                        env.DetailsLastSyncedUtc = cached.Value.syncedUtc;
                        return snap.Hydrate(envAssets);
                    }
                    _catalog.DeleteEnvDetails(env.EnvId);
                }
                else
                {
                    // Corrupted JSON (rare — a future build's serialization went
                    // sideways). Drop the row and fall through to live fetch.
                    _catalog.DeleteEnvDetails(env.EnvId);
                }
            }
        }
        else
        {
            // Force-refresh: drop the row first so a failure leaves no cache.
            _catalog.DeleteEnvDetails(env.EnvId);
            // Also clear the previous enrichments stamped on the AssetRow
            // instances so the new fetch fills them in cleanly. (Status etc.
            // come back stale-shaped only if Dataverse partially fails.)
            foreach (var a in envAssets)
            {
                a.Status       = null;
                a.IsPremium    = null;
                a.DlpStatus    = null;
                a.SolutionName = null;
                a.IsManaged    = null;
            }
        }

        // Kick the DLP fetch in PARALLEL with the Dataverse fan-out instead of
        // awaiting it first. AssetMeta inside DataverseEnvClient awaits this
        // task only when it actually needs the policies (canvas-app DLP eval),
        // so Solutions / Power Pages / Users no longer wait on BAP for env #1.
        // After env #1, the service-side cache makes the task instantly
        // resolved and the overlap is free.
        Task<IReadOnlyList<BapDlpClient.DlpPolicyDto>?> dlpTask = Task.Run(async () =>
        {
            try { return await LoadDlpPoliciesAsync(ct).ConfigureAwait(false); }
            catch { return null; /* policy.read missing or BAP transient */ }
        }, ct);

        var client = new DataverseEnvClient(_auth, _diagnostics);
        var details = await client.LoadAllAsync(env.EnvId, env.InstanceUrl, envAssets, dlpTask, ct).ConfigureAwait(false);

        // Persist the freshly-fetched snapshot. Best-effort: if SQLite write
        // fails (disk full, file locked) we still hand the live result back
        // to the caller — caching is an optimisation, not a correctness gate.
        try
        {
            var syncedUtc = DateTime.UtcNow;
            var snap = EnvDetailsSnapshot.Capture(details, envAssets, syncedUtc);
            _catalog.SaveEnvDetails(env.EnvId, snap.Serialize(), syncedUtc);
            env.DetailsLastSyncedUtc = syncedUtc;
        }
        catch
        {
            // Cache write failed — proceed with the in-memory result.
        }

        return details;
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

    // Cached DLP policy list — second call returns the same snapshot. Cleared
    // implicitly on next refresh because the service instance is rebuilt
    // alongside the catalog read.
    private IReadOnlyList<BapDlpClient.DlpPolicyDto>? _dlpPolicies;

    public async Task<IReadOnlyList<BapDlpClient.DlpPolicyDto>> LoadDlpPoliciesAsync(CancellationToken ct = default)
    {
        if (_dlpPolicies is not null) return _dlpPolicies;
        var client = new BapDlpClient(_auth, _diagnostics);
        var policies = await client.ListPoliciesAsync(ct: ct).ConfigureAwait(false);
        _dlpPolicies = policies;
        return policies;
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

    public async Task<IReadOnlyDictionary<string, string>> ResolveSecurityGroupNamesAsync(
        IEnumerable<string> groupIds,
        CancellationToken ct = default)
    {
        // Same pattern — share the cached client when present.
        var client = _graphLicenses ?? new GraphLicenseClient(_auth);
        return await client.ResolveGroupNamesAsync(groupIds, ct).ConfigureAwait(false);
    }

    public async Task RevokeSystemAdminAsync(
        string instanceUrl,
        string systemUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceUrl))
            throw new InvalidOperationException("Cannot revoke admin — environment has no Dataverse instance URL.");
        if (string.IsNullOrWhiteSpace(systemUserId))
            throw new InvalidOperationException("Cannot revoke admin — systemuserid is required.");

        // Token is per-env — Dataverse uses the env origin as the audience.
        var origin = new Uri(instanceUrl).GetLeftPart(UriPartial.Authority);
        var scope  = origin + "/.default";
        var token  = await _auth.GetTokenAsync(scope, ct).ConfigureAwait(false);

        var inner = new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        using var http = new HttpClient(inner, disposeHandler: true)
        {
            BaseAddress = new Uri(origin + "/api/data/v9.2/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        http.DefaultRequestHeaders.Add("OData-Version", "4.0");

        // 1. Resolve the System Administrator role id on this env. Roles are
        //    per-env in Dataverse so we cannot cache the id across orgs.
        using var roleResp = await http.GetAsync(
            "roles?$select=roleid&$top=1&$filter=name eq 'System Administrator'", ct).ConfigureAwait(false);
        if (!roleResp.IsSuccessStatusCode)
        {
            var body = await roleResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Could not resolve System Administrator role id ({(int)roleResp.StatusCode} {roleResp.ReasonPhrase}). Body: {body}");
        }
        string? roleId = null;
        using (var roleDoc = System.Text.Json.JsonDocument.Parse(
            await roleResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
        {
            if (roleDoc.RootElement.TryGetProperty("value", out var arr) &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array &&
                arr.GetArrayLength() > 0 &&
                arr[0].TryGetProperty("roleid", out var idEl))
            {
                roleId = idEl.GetString();
            }
        }
        if (string.IsNullOrEmpty(roleId))
            throw new InvalidOperationException("System Administrator role not found on this environment.");

        // 2. Disassociate the role from the user. Dataverse expects the full
        //    URI of the related role record in $id.
        var deleteUrl =
            $"systemusers({systemUserId})/systemuserroles_association/$ref" +
            $"?$id={origin}/api/data/v9.2/roles({roleId})";
        using var deleteResp = await http.DeleteAsync(deleteUrl, ct).ConfigureAwait(false);
        if (!deleteResp.IsSuccessStatusCode)
        {
            var body = await deleteResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Revoke admin failed ({(int)deleteResp.StatusCode} {deleteResp.ReasonPhrase}). Body: {body}");
        }
    }

    public async Task<RefreshResult> RefreshAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => await RefreshAsync(progress, onPhaseReady: null, ct).ConfigureAwait(false);

    public async Task<RefreshResult> RefreshAsync(
        IProgress<string>? progress,
        Func<RefreshPhase, Task>? onPhaseReady,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _diagnostics.ResetLog();
        progress?.Report($"Trace log: {_diagnostics.LogPath}");

        // Pre-warm both audiences in parallel so the first request of each
        // phase doesn't sit on a cold MSAL silent acquire. PPAC scope is also
        // used by the Inventory API; BAP is its own audience.
        progress?.Report("Acquiring tokens (PPAC + BAP)...");
        var ppacTokenTask = _auth.GetTokenAsync(PpacScope, ct);
        var bapTokenTask  = _auth.GetTokenAsync("https://service.powerapps.com/.default", ct);
        try { await Task.WhenAll(ppacTokenTask, bapTokenTask).ConfigureAwait(false); }
        catch { /* one may fail; let the per-phase code surface the real error */ }

        var sc = await BuildClientAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Phase A: environments + per-env BAP capacity. These are joined on
        // env_id, so we resolve them together and persist as a unit. Note we
        // start the BAP call concurrently with the env list — they don't
        // depend on each other.
        var envPhase = Task.Run(async () =>
        {
            progress?.Report("Listing environments (PPAC)...");
            var envListTask = sc.Environmentmanagement.Environments.GetAsync(cancellationToken: ct);
            var bapTask = Task.Run<IReadOnlyDictionary<string, IReadOnlyList<CapacityEntry>>>(async () =>
            {
                try
                {
                    var bap = new BapCapacityClient(_auth, _diagnostics);
                    return await bap.GetCapacityByEnvAsync(now, progress, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    progress?.Report($"  (BAP capacity failed: {ex.GetType().Name}: {ex.Message}) — continuing without it.");
                    return new Dictionary<string, IReadOnlyList<CapacityEntry>>(StringComparer.OrdinalIgnoreCase);
                }
            }, ct);

            var envList = await envListTask.ConfigureAwait(false);
            var rawEnvs = ExtractList(envList);
            var envRows = new List<EnvironmentRow>();
            foreach (var raw in rawEnvs)
            {
                ct.ThrowIfCancellationRequested();
                var row = MapEnvironment(raw, now);
                if (row is not null) envRows.Add(row);
            }
            progress?.Report($"PPAC: {envRows.Count} environments mapped.");

            var byEnv = await bapTask.ConfigureAwait(false);
            var capRows = new List<CapacityEntry>();
            foreach (var row in envRows)
                if (byEnv.TryGetValue(row.EnvId, out var rows) && rows.Count > 0)
                    capRows.AddRange(rows);
            progress?.Report($"BAP capacity: {byEnv.Count}/{envRows.Count} envs reported capacity.");

            // Land env + capacity together so the grid can show storage GBs
            // alongside the env names on first paint.
            _catalog.ReplaceEnvironments(envRows);
            _catalog.ReplaceCapacity(capRows);
            if (onPhaseReady is not null)
                await onPhaseReady(RefreshPhase.EnvironmentsAndCapacity).ConfigureAwait(false);

            return (envRows.Count, capRows.Count);
        }, ct);

        // Phase B: tenant-wide capacity rollup. Independent of envs.
        var tenantPhase = Task.Run(async () =>
        {
            try
            {
                progress?.Report("Fetching tenant-wide capacity...");
                var tenantCap = await sc.Licensing.TenantCapacity.GetAsync(cancellationToken: ct).ConfigureAwait(false);
                var rows = MapTenantCapacity(tenantCap, now).ToList();
                _catalog.ReplaceTenantCapacity(rows);
                if (onPhaseReady is not null)
                    await onPhaseReady(RefreshPhase.TenantCapacity).ConfigureAwait(false);
                return rows.Count;
            }
            catch (Exception ex)
            {
                progress?.Report($"  (tenant capacity failed: {ex.GetType().Name}: {ex.Message})");
                return 0;
            }
        }, ct);

        // Phase B2: per-currency tenant capacity report. Same domain as
        // tenant capacity but a separate SDK endpoint — runs in parallel.
        var currencyPhase = Task.Run(async () =>
        {
            try
            {
                progress?.Report("Fetching tenant capacity currency reports...");
                var currencyResp = await sc.Licensing.TenantCapacity.CurrencyReports.GetAsync(cancellationToken: ct).ConfigureAwait(false);
                var rows = MapCurrencyReports(currencyResp, now).ToList();
                _catalog.ReplaceCurrencyReports(rows);
                if (onPhaseReady is not null)
                    await onPhaseReady(RefreshPhase.CurrencyReports).ConfigureAwait(false);
                progress?.Report($"Currency reports: {rows.Count} currency code(s).");
                return rows.Count;
            }
            catch (Exception ex)
            {
                progress?.Report($"  (currency reports failed: {ex.GetType().Name}: {ex.Message})");
                return 0;
            }
        }, ct);

        // Phase B3: pay-as-you-go billing policies + their attached-env
        // counts. The list call is one round-trip; the per-policy
        // /environments fan-out runs in parallel with bounded concurrency.
        var billingPhase = Task.Run(async () =>
        {
            try
            {
                progress?.Report("Fetching billing policies...");
                var bpResp = await sc.Licensing.BillingPolicies.GetAsync(cancellationToken: ct).ConfigureAwait(false);
                var rows = MapBillingPolicies(bpResp, now).ToList();
                if (rows.Count > 0)
                {
                    progress?.Report($"Billing policies: {rows.Count} found; fetching attached envs...");
                    using var gate = new SemaphoreSlim(6);
                    var tasks = rows.Select(async r =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var envResp = await sc.Licensing.BillingPolicies[r.PolicyId].Environments
                                .GetAsync(cancellationToken: ct).ConfigureAwait(false);
                            r.AttachedEnvironmentCount = CountList(envResp);
                        }
                        catch
                        {
                            // 404 / 403 on per-policy envs (e.g. policy
                            // has no envs, or caller lacks rights) — leave
                            // count at 0, don't fail the whole phase.
                        }
                        finally { gate.Release(); }
                    }).ToArray();
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                _catalog.ReplaceBillingPolicies(rows);
                if (onPhaseReady is not null)
                    await onPhaseReady(RefreshPhase.BillingPolicies).ConfigureAwait(false);
                return rows.Count;
            }
            catch (Exception ex)
            {
                progress?.Report($"  (billing policies failed: {ex.GetType().Name}: {ex.Message})");
                return 0;
            }
        }, ct);

        // Phase C: tenant-wide asset catalog (Inventory API). Slowest phase
        // by far. Runs concurrently with phases A and B so the user sees the
        // env grid + tiles populate while assets stream in the background.
        var assetPhase = Task.Run(async () =>
        {
            try
            {
                progress?.Report("Fetching tenant-wide assets (Power Platform Inventory API)...");
                var inv = new InventoryApiClient(_auth, _diagnostics);
                var pulled = await inv.GetAllAssetsAsync(now, progress, ct).ConfigureAwait(false);
                _catalog.ReplaceAssets(pulled);
                if (onPhaseReady is not null)
                    await onPhaseReady(RefreshPhase.Assets).ConfigureAwait(false);
                return pulled.Count;
            }
            catch (Exception ex)
            {
                progress?.Report($"  (Inventory API failed: {ex.GetType().Name}: {ex.Message}) — continuing without asset catalog.");
                return 0;
            }
        }, ct);

        var (envCount, capCount) = await envPhase.ConfigureAwait(false);
        await tenantPhase.ConfigureAwait(false);
        await currencyPhase.ConfigureAwait(false);
        await billingPhase.ConfigureAwait(false);
        var assetCount = await assetPhase.ConfigureAwait(false);

        sw.Stop();
        progress?.Report($"All phases done in {sw.Elapsed.TotalSeconds:0.0}s.");
        return new RefreshResult(envCount, capCount, assetCount, sw.Elapsed);
    }

    // ------------------------------------------------------------------
    // ServiceClient construction.
    // ------------------------------------------------------------------
    private async Task<ServiceClient> BuildClientAsync(CancellationToken ct)
    {
        // Re-acquire the token per refresh so MSAL silent-cache expiry handles itself.
        var token = await _auth.GetTokenAsync(PpacScope, ct).ConfigureAwait(false);
        var tokenProvider = new StaticTokenAccessProvider(token);
        var authProv = new BaseBearerTokenAuthenticationProvider(tokenProvider);

        var handlers = KiotaClientFactory.CreateDefaultHandlers();
        // Use the SDK-provided ApiVersionHandler. Kiota pre-adds "?api-version="
        // with an empty value when QueryParameters.ApiVersion isn't set; the
        // SDK handler replaces the empty value, our local one only added when
        // missing -> 400 ApiVersionInvalid.
        handlers.Insert(0, new Microsoft.PowerPlatform.Management.ApiVersionHandler(ApiVersion));
        // IMPORTANT: do NOT insert the singleton _diagnostics handler here.
        // DelegatingHandler.InnerHandler can only be set ONCE per instance,
        // so re-using _diagnostics across refreshes throws on the 2nd refresh
        // ("This instance has already started one or more requests. Properties
        // can only be modified before sending the first request."). Build a
        // fresh per-refresh handler that writes to the same trace file and
        // shares the dump-suppression predicate. Same workaround used by
        // BapCapacityClient — see its constructor xml-doc for the rationale.
        var perRefreshDiag = new HttpDiagnosticsHandler
        {
            ShouldDumpFailure = _diagnostics.ShouldDumpFailure
        };
        handlers.Insert(0, perRefreshDiag); // outermost so it sees the final response

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

    /// <summary>
    /// Map the per-currency tenant capacity report response into our
    /// flattened <see cref="TenantCurrencyReportEntry"/> rows. The SDK
    /// returns one item per currency code with purchased / allocated /
    /// consumed totals (units depend on the underlying SKU). Reflection-
    /// based so we tolerate SDK field renames.
    /// </summary>
    private static IEnumerable<TenantCurrencyReportEntry> MapCurrencyReports(object? root, DateTime now)
    {
        if (root is null) yield break;
        // Response shape varies: sometimes top-level "Value" list,
        // sometimes a wrapper with "CurrencyReports" list. Try both.
        var items = ExtractList(root);
        if (items.Count == 0)
        {
            var rt = root.GetType();
            var listProp = rt.GetProperty("CurrencyReports", BindingFlags.Public | BindingFlags.Instance)
                           ?? rt.GetProperty("Reports",       BindingFlags.Public | BindingFlags.Instance);
            if (listProp?.GetValue(root) is System.Collections.IEnumerable e)
            {
                var l = new List<object>();
                foreach (var x in e) if (x is not null) l.Add(x);
                items = l;
            }
        }

        foreach (var item in items)
        {
            if (item is null) continue;
            var it = item.GetType();
            var code = ToStringOrNull(Get<object>(item, it, "CurrencyCode"))
                       ?? ToStringOrNull(Get<object>(item, it, "Currency"))
                       ?? ReadAdditionalString(item, it, "currencyCode")
                       ?? ReadAdditionalString(item, it, "currency");
            if (string.IsNullOrWhiteSpace(code)) code = "Unknown";

            yield return new TenantCurrencyReportEntry
            {
                CurrencyCode  = code,
                Purchased     = AsDouble(Get<object>(item, it, "PurchasedCapacity"))
                                ?? AsDouble(Get<object>(item, it, "Purchased"))
                                ?? AsDouble(Get<object>(item, it, "MaxCapacity")),
                Allocated     = AsDouble(Get<object>(item, it, "AllocatedCapacity"))
                                ?? AsDouble(Get<object>(item, it, "Allocated"))
                                ?? AsDouble(Get<object>(item, it, "TotalCapacity")),
                Consumed      = AsDouble(Get<object>(item, it, "ConsumedCapacity"))
                                ?? AsDouble(Get<object>(item, it, "Consumed"))
                                ?? AsDouble(Get<object>(item, it, "TotalConsumption")),
                LastSyncedUtc = now
            };
        }
    }

    /// <summary>
    /// Map the billing policies list response into our flattened
    /// <see cref="BillingPolicyRow"/> rows. Reads BillingInstrument as a
    /// nested object — its sub-fields name the Azure subscription / RG
    /// the policy bills against.
    /// </summary>
    private static IEnumerable<BillingPolicyRow> MapBillingPolicies(object? root, DateTime now)
    {
        if (root is null) yield break;
        foreach (var item in ExtractList(root))
        {
            var it = item.GetType();
            var id = Get<string>(item, it, "Id") ?? ReadAdditionalString(item, it, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            string? subId = null, rg = null, resId = null;
            var bi = Get<object>(item, it, "BillingInstrument");
            if (bi is not null)
            {
                var bt = bi.GetType();
                subId = Get<string>(bi, bt, "SubscriptionId") ?? ReadAdditionalString(bi, bt, "subscriptionId");
                rg    = Get<string>(bi, bt, "ResourceGroup")  ?? ReadAdditionalString(bi, bt, "resourceGroup");
                resId = Get<string>(bi, bt, "ResourceId")
                        ?? Get<string>(bi, bt, "Id")
                        ?? ReadAdditionalString(bi, bt, "resourceId");
            }

            yield return new BillingPolicyRow
            {
                PolicyId                        = id,
                Name                            = Get<string>(item, it, "Name"),
                Location                        = Get<string>(item, it, "Location"),
                Status                          = ToStringOrNull(Get<object>(item, it, "Status")),
                BillingInstrumentSubscriptionId = subId,
                BillingInstrumentResourceGroup  = rg,
                BillingInstrumentResourceId     = resId,
                AttachedEnvironmentCount        = 0,
                LastSyncedUtc                   = now
            };
        }
    }

    /// <summary>
    /// Counts items in a Kiota list response (Value enumerable). Used by
    /// the billing-policy /environments fan-out where we only care about
    /// the attached env count, not the env contents.
    /// </summary>
    private static int CountList(object? response)
    {
        if (response is null) return 0;
        var t = response.GetType();
        var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProp?.GetValue(response) is System.Collections.IEnumerable e)
        {
            int n = 0;
            foreach (var _ in e) n++;
            return n;
        }
        return 0;
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
