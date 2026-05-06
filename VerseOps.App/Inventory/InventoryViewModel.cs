using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using VerseOps.App.Inventory.Models;
using VerseOps.App.Inventory.Services;

namespace VerseOps.App.Inventory;

/// <summary>
/// View model for the inventory dashboard. Hydrates from local SQLite on
/// construction; refresh kicks a PPAC-SDK pull and reloads from SQLite.
/// </summary>
public sealed class InventoryViewModel : INotifyPropertyChanged
{
    private readonly IInventoryService _service;
    private bool _isBusy;
    private string _statusText = "Idle.";
    private string? _lastError;
    private DateTime? _lastRefreshedUtc;
    private bool _showOnlyMyEnvironments;
    private bool _membershipProbeStarted;

    /// <summary>
    /// True when at least one EnvironmentRow has IsExpanded==true. Drives
    /// the "focus mode" branch in <see cref="FilterEnvironment"/> so that
    /// expanding a row hides every other env row, giving the open detail
    /// panel the full vertical real estate. Recomputed by
    /// <see cref="OnEnvironmentRowExpansionChanged"/>.
    /// </summary>
    private bool _anyRowExpanded;

    /// <summary>
    /// Last-known Graph license client (null until first env detail load
    /// triggers it, or the Licenses drawer is opened). Cached here so the
    /// hero tile + drawer can read SKU consumption without an extra round
    /// trip; the underlying service also caches at the singleton level so
    /// even a fresh client just reuses the parsed payload.
    /// </summary>
    private GraphLicenseClient? _cachedGraph;

    public InventoryViewModel(IInventoryService service)
    {
        _service = service;
        Environments = new ObservableCollection<EnvironmentRow>();
        TenantCapacity = new ObservableCollection<TenantCapacityEntry>();
        Assets = new ObservableCollection<AssetRow>();
        DrawerItems = new ObservableCollection<DrawerItem>();

        // Wrap Environments in a CollectionView so the env DataGrid can be
        // filtered live (toggle: "only show envs I'm a Dataverse user in").
        // The toggle setter calls EnvironmentsView.Refresh() to re-evaluate
        // the predicate; the predicate itself reads ShowOnlyMyEnvironments +
        // each row's IsCurrentUserMember tri-state.
        EnvironmentsView = CollectionViewSource.GetDefaultView(Environments);
        EnvironmentsView.Filter = FilterEnvironment;

        // Subscribe to per-row IsExpanded changes so we can hide other env
        // rows when one is opened ("focus mode"). Re-subscribe whenever the
        // backing collection mutates (Refresh / Reload re-creates rows).
        Environments.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (EnvironmentRow r in e.OldItems)
                    r.PropertyChanged -= OnEnvironmentRowExpansionChanged;
            if (e.NewItems != null)
                foreach (EnvironmentRow r in e.NewItems)
                    r.PropertyChanged += OnEnvironmentRowExpansionChanged;
            RecomputeAnyRowExpanded();
        };

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        ReloadCommand = new RelayCommand(_ => ReloadFromCatalog(), _ => !IsBusy);
        OpenTraceLogCommand = new RelayCommand(_ => OpenTraceLog());
        CopyErrorCommand = new RelayCommand(_ => CopyError(), _ => HasError);
        OpenDrawerCommand = new RelayCommand(p => OpenDrawer(p as string));
        CloseDrawerCommand = new RelayCommand(_ => CloseDrawer());
        InspectJsonCommand = new RelayCommand(p => InspectJson(p));
        OpenInMakerCommand = new RelayCommand(p => OpenInMaker(p));
        OpenEnvCommand = new RelayCommand(p => OpenEnv(p as EnvironmentRow));
        CopyTextCommand = new RelayCommand(p => CopyText(p as string));
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        ReloadFromCatalog();
    }

    public ObservableCollection<EnvironmentRow> Environments { get; }

    /// <summary>
    /// Filtered/sorted view of <see cref="Environments"/>. The env DataGrid
    /// binds to this so the "Only my environments" toggle can hide rows
    /// without disturbing the underlying collection (so cached SQLite data
    /// stays consistent and other consumers — hero tiles, drawer — still see
    /// the full set).
    /// </summary>
    public ICollectionView EnvironmentsView { get; }

    public ObservableCollection<TenantCapacityEntry> TenantCapacity { get; }

    /// <summary>Tenant-wide asset cache (apps + flows + agents) from the Inventory API.</summary>
    public ObservableCollection<AssetRow> Assets { get; }

    /// <summary>Items rendered inside the right-side drawer when a hero KPI is clicked.</summary>
    public ObservableCollection<DrawerItem> DrawerItems { get; }

    public ICommand RefreshCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand OpenTraceLogCommand { get; }
    public ICommand CopyErrorCommand { get; }
    public ICommand OpenDrawerCommand { get; }
    public ICommand CloseDrawerCommand { get; }

    /// <summary>Pop the Metadata Inspector dialog showing the row's raw JSON.</summary>
    public ICommand InspectJsonCommand { get; }

    /// <summary>Open the row in the appropriate maker portal in the default browser.</summary>
    public ICommand OpenInMakerCommand { get; }

    /// <summary>Open the env's <c>make.powerapps.com</c> home page in the default browser.</summary>
    public ICommand OpenEnvCommand { get; }

    /// <summary>Copy any string parameter (env id, instance URL, etc.) to the clipboard.</summary>
    public ICommand CopyTextCommand { get; }

    /// <summary>Open any string URL in the default browser (Instance URL, maker links, etc.).</summary>
    public ICommand OpenUrlCommand { get; }

    // ------------------------------------------------------------------
    // "Only my environments" toggle. When true, the env DataGrid is
    // filtered to envs that have an Azure AD security group AND the
    // signed-in user is a transitive member of that group (single Graph
    // checkMemberGroups call). Envs without a security group are hidden
    // entirely on the assumption they're "open" envs not specific to a
    // team. Membership decisions are cached on the row tri-state so
    // re-toggling is instant.
    // ------------------------------------------------------------------
    public bool ShowOnlyMyEnvironments
    {
        get => _showOnlyMyEnvironments;
        set
        {
            if (_showOnlyMyEnvironments == value) return;
            _showOnlyMyEnvironments = value;
            OnPropertyChanged();
            EnvironmentsView.Refresh();
            if (value) _ = EnsureMembershipProbeAsync();
        }
    }

    /// <summary>
    /// CollectionView predicate. When the toggle is off everything passes;
    /// when it's on we keep only envs that have a security group AND the
    /// signed-in user is confirmed to be in it. Rows whose security-group
    /// check hasn't completed yet (tri-state null) are hidden so the list
    /// fills in monotonically as Graph results arrive.
    /// </summary>
    /// <summary>
    /// PropertyChanged handler attached to every <see cref="EnvironmentRow"/>
    /// so a row expanding/collapsing immediately re-runs the env-grid
    /// filter and applies "focus mode" (other rows hidden while one is open).
    /// </summary>
    private void OnEnvironmentRowExpansionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EnvironmentRow.IsExpanded)) return;
        RecomputeAnyRowExpanded();
    }

    private void RecomputeAnyRowExpanded()
    {
        var any = Environments.Any(r => r.IsExpanded);
        if (any == _anyRowExpanded) return;
        _anyRowExpanded = any;
        // Refresh on the UI thread; CollectionChanged + INPC handlers can
        // be raised from background workers (e.g., the per-row Dataverse
        // load) so we marshal defensively.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(() => EnvironmentsView.Refresh()));
        else
            EnvironmentsView.Refresh();
    }

    /// <summary>
    /// Predicate for the <see cref="EnvironmentsView"/> ICollectionView. Two
    /// independent gates, ANDed together:
    ///
    ///   * "Only my environments" toggle (security-group strict mode) —
    ///     when on, we keep only envs that have a security group AND the
    ///     signed-in user is confirmed to be in it. Rows whose security-
    ///     group check hasn't completed yet (tri-state null) are hidden so
    ///     the list fills in monotonically as Graph results arrive.
    ///   * "Focus mode" — when *any* env row is expanded, hide every other
    ///     row so the expanded one has the full vertical real estate. The
    ///     moment the user collapses it the list pops back to normal. This
    ///     is driven by <see cref="OnEnvironmentRowExpansionChanged"/>,
    ///     wired into each row's INPC at <see cref="HookExpansionTracking"/>.
    /// </summary>
    private bool FilterEnvironment(object item)
    {
        if (item is not EnvironmentRow row) return false;

        // Security-group strict mode.
        if (_showOnlyMyEnvironments &&
            !(!string.IsNullOrEmpty(row.SecurityGroupId) && row.IsCurrentUserInSecurityGroup == true))
        {
            return false;
        }

        // Focus mode: when at least one row is expanded, only that row
        // (well, all expanded rows — supports multi-expand) stays visible.
        if (_anyRowExpanded && !row.IsExpanded) return false;

        return true;
    }

    /// <summary>
    /// One-shot Graph <c>checkMemberGroups</c> call against every distinct
    /// env security-group id. Populates <see cref="EnvironmentRow.IsCurrentUserInSecurityGroup"/>
    /// on each row so the filter predicate can decide what to keep. Runs at
    /// most once per session; subsequent toggle flips re-use the cached
    /// per-row values.
    /// </summary>
    private async Task EnsureMembershipProbeAsync()
    {
        if (_membershipProbeStarted) return;
        _membershipProbeStarted = true;
        try
        {
            StatusText = "Checking environment security-group membership (Graph)...";

            // Distinct group ids across every env (envs without a group are
            // already hidden by the filter and don't need a Graph call).
            var groupIds = Environments
                .Select(e => e.SecurityGroupId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groupIds.Count == 0)
            {
                StatusText = "No environments have a security group attached — toggle off to see them all.";
                return;
            }

            var matched = await _service.CheckSecurityGroupMembershipAsync(groupIds).ConfigureAwait(false);

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var row in Environments)
                {
                    if (string.IsNullOrEmpty(row.SecurityGroupId))
                    {
                        // No group → not a candidate; null means "not applicable".
                        row.IsCurrentUserInSecurityGroup = null;
                    }
                    else
                    {
                        row.IsCurrentUserInSecurityGroup = matched.Contains(row.SecurityGroupId);
                    }
                }
                EnvironmentsView.Refresh();
            });

            var memberCount = Environments.Count(e => e.IsCurrentUserInSecurityGroup == true);
            StatusText = $"Security-group check done: you are a member of {memberCount}/{groupIds.Count} group(s) — showing matching environments.";
        }
        catch (Exception ex)
        {
            StatusText = $"Security-group membership check failed: {ex.Message}";
            // Allow a future retry by clearing the latch.
            _membershipProbeStarted = false;
        }
    }

    /// <summary>Open an environment row's maker home in the default browser.</summary>
    private void OpenEnv(EnvironmentRow? env)
    {
        if (env is null || string.IsNullOrEmpty(env.MakerUrl))
        {
            StatusText = "No env URL available for this row.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(env.MakerUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open env in browser: {ex.Message}";
        }
    }

    /// <summary>Copy an arbitrary string (env id / instance URL / token / etc.) to the clipboard.</summary>
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = $"Copied {text.Length} chars to clipboard.";
        }
        catch (Exception ex)
        {
            try { System.Windows.Clipboard.SetDataObject(text, copy: true); StatusText = "Copied to clipboard."; }
            catch { StatusText = $"Copy failed: {ex.Message}"; }
        }
    }

    /// <summary>Open an arbitrary URL in the default browser. Used by the
    /// instance-URL launch icon and any other "view in browser" affordance
    /// that doesn't have a dedicated command.</summary>
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { StatusText = "No URL to open."; return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open URL: {ex.Message}";
        }
    }

    private bool _isDrawerOpen;
    private string? _drawerTitle;
    private string? _drawerSubtitle;

    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        private set { if (_isDrawerOpen == value) return; _isDrawerOpen = value; OnPropertyChanged(); }
    }
    public string? DrawerTitle
    {
        get => _drawerTitle;
        private set { if (_drawerTitle == value) return; _drawerTitle = value; OnPropertyChanged(); }
    }
    public string? DrawerSubtitle
    {
        get => _drawerSubtitle;
        private set { if (_drawerSubtitle == value) return; _drawerSubtitle = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Populate the drawer based on which hero card was clicked. CommandParameter
    /// is the kind ("storage" / "governance" / "assets"). Today we surface real
    /// data for storage (per-env GB ranking) and stub the rest until DLP / asset
    /// pulls land.
    /// </summary>
    private void OpenDrawer(string? kind)
    {
        DrawerItems.Clear();
        switch ((kind ?? string.Empty).ToLowerInvariant())
        {
            case "storage":
                DrawerTitle    = "Storage Drill-down";
                DrawerSubtitle = "Top environments by Database GB consumed (BAP /scopes/admin/environments?$expand=properties/capacity).";
                foreach (var row in Environments
                            .Where(e => e.DatabaseGb.HasValue)
                            .OrderByDescending(e => e.DatabaseGb!.Value)
                            .Take(50))
                {
                    DrawerItems.Add(new DrawerItem(
                        row.DisplayName ?? row.EnvId,
                        $"DB: {row.DatabaseGb:N2} GB   File: {row.FileGb:N2} GB   Log: {row.LogGb:N3} GB",
                        row.Sku ?? string.Empty));
                }
                if (DrawerItems.Count == 0)
                    DrawerItems.Add(new DrawerItem("No capacity data yet", "Click Refresh to pull from BAP.", string.Empty));
                break;

            case "governance":
                DrawerTitle    = "Tenant Governance: DLP Policies";
                DrawerSubtitle = "DLP policy enumeration ships in v1.6 — placeholder list shown.";
                for (int i = 1; i <= 8; i++)
                    DrawerItems.Add(new DrawerItem($"Default Policy {i}", "Created: N/A   Rule Sets: 1 active configurations   Scope: All Environments", "ENFORCED"));
                break;

            case "assets":
                DrawerTitle    = "Total Assets";
                DrawerSubtitle = $"{Assets.Count} assets across {Environments.Count} environments " +
                                 "(Power Platform Inventory API, single tenant-wide query).";
                // Top-line counts grouped by type, then a sample of the most
                // recently modified items for quick visual inspection.
                foreach (var grp in Assets
                            .GroupBy(a => a.AssetType, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Count()))
                {
                    var sample = grp.First();
                    DrawerItems.Add(new DrawerItem(
                        sample.TypeDisplay,
                        $"{grp.Count():N0} total tenant-wide   • most recent: {grp.OrderByDescending(a => a.ModifiedUtc ?? a.CreatedUtc ?? DateTime.MinValue).First().DisplayName}",
                        grp.Count().ToString("N0")));
                }
                if (DrawerItems.Count == 0)
                    DrawerItems.Add(new DrawerItem("No assets cached", "Click Refresh to pull from the Power Platform Inventory API.", string.Empty));
                break;

            case "licenses":
                // Tenant-wide license consumption rollup. Pulls the cached
                // Graph license map (one paged trip across the directory)
                // and groups by SKU. Triggers the Graph fetch on demand if
                // it hasn't run yet so opening this drawer first time is
                // self-contained.
                DrawerTitle    = "Licenses Consumed";
                DrawerSubtitle = "Tenant-wide rollup of Microsoft 365 / Power Platform SKUs assigned to users " +
                                 "(distinct UPNs per SKU, decoded from Microsoft Graph subscribedSkus catalog).";
                _ = LoadLicenseDrawerAsync();
                break;

            case "environments":
            default:
                DrawerTitle    = "Environments";
                DrawerSubtitle = $"{Environments.Count} environments synced. Use the grid below to filter / sort.";
                foreach (var row in Environments.Take(50))
                {
                    DrawerItems.Add(new DrawerItem(
                        row.DisplayName ?? row.EnvId,
                        $"{row.Sku}   {row.Region}   {row.ProvisioningState}",
                        row.IsDefault ? "DEFAULT" : string.Empty));
                }
                break;
        }
        IsDrawerOpen = true;
    }

    private void CloseDrawer() => IsDrawerOpen = false;

    public sealed record DrawerItem(string Title, string Subtitle, string Badge);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ReloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    public string? LastError
    {
        get => _lastError;
        private set { if (_lastError == value) return; _lastError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); (CopyErrorCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    public bool HasError => !string.IsNullOrEmpty(_lastError);

    public DateTime? LastRefreshedUtc
    {
        get => _lastRefreshedUtc;
        private set { if (_lastRefreshedUtc == value) return; _lastRefreshedUtc = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastRefreshedDisplay)); }
    }

    public string LastRefreshedDisplay
        => _lastRefreshedUtc.HasValue
            ? $"Last refreshed: {_lastRefreshedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "Never refreshed.";

    public int EnvironmentCount => Environments.Count;

    /// <summary>Tenant-wide asset count used by the "Total Assets" hero tile.</summary>
    public int AssetCount => Assets.Count;

    public string AssetCountDisplay => Assets.Count == 0 ? "—" : Assets.Count.ToString("N0");

    /// <summary>
    /// Tenant-wide count of distinct users with at least one Microsoft 365 /
    /// Power Platform license assigned. Powered by the cached Graph fetch
    /// (<see cref="IInventoryService.LoadGraphLicensesAsync"/>) so it lights
    /// up after the first env detail load enrich-step has triggered the
    /// directory pull. Drives the new "Licenses Consumed" hero tile.
    /// </summary>
    public int LicensedUserCount => _cachedGraph?.LicensedUserCount ?? 0;

    public string LicensedUserCountDisplay
        => LicensedUserCount == 0 ? "—" : LicensedUserCount.ToString("N0");

    /// <summary>
    /// Returns the cached assets for a given environment, ordered by most-
    /// recently-modified first. Wired into the row-details expander template
    /// so opening a row instantly shows that env's apps/flows/agents from the
    /// already-loaded SQLite cache (no extra HTTP round-trip).
    /// </summary>
    public IReadOnlyList<AssetRow> GetAssetsForEnvironment(string envId)
    {
        if (string.IsNullOrEmpty(envId)) return Array.Empty<AssetRow>();
        return Assets
            .Where(a => string.Equals(a.EnvId, envId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.ModifiedUtc ?? a.CreatedUtc ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>Tenant-wide DB capacity in GB — sum of per-env DB GB across all envs.</summary>
    public string DatabaseGbDisplay  => FormatTenantTotal("Database");
    public string FileGbDisplay      => FormatTenantTotal("File");
    public string LogGbDisplay       => FormatTenantTotal("Log");

    /// <summary>
    /// Show the tenant-wide rollup. We prefer summing the per-env BAP rows
    /// already loaded into <see cref="Environments"/> (matches what the user
    /// sees in the grid). If those are missing for some reason, fall back to
    /// the tenant-wide row that PPAC <c>Licensing.TenantCapacity</c> returned.
    /// </summary>
    private string FormatTenantTotal(string kind)
    {
        double? sum = kind switch
        {
            "Database" => Environments.Sum(e => e.DatabaseGb ?? 0),
            "File"     => Environments.Sum(e => e.FileGb     ?? 0),
            "Log"      => Environments.Sum(e => e.LogGb      ?? 0),
            _          => null
        };

        if (sum.HasValue && sum.Value > 0)
        {
            // Pull the matching tenant-wide max if PPAC supplied one.
            var tenantRow = TenantCapacity.FirstOrDefault(c =>
                string.Equals(c.CapacityType, kind, StringComparison.OrdinalIgnoreCase));
            if (tenantRow != null)
            {
                var maxGb = (tenantRow.MaxCapacity ?? tenantRow.TotalCapacity ?? 0) / 1024.0;
                if (maxGb > 0)
                    return $"{sum.Value:N1} / {maxGb:N0} GB";
            }
            return $"{sum.Value:N1} GB";
        }

        // Per-env data missing — fall back to PPAC tenant-wide row.
        var row = TenantCapacity.FirstOrDefault(c =>
            string.Equals(c.CapacityType, kind, StringComparison.OrdinalIgnoreCase));
        if (row is null) return "—";
        var used = (row.Consumed ?? 0) / 1024.0;
        var max  = (row.MaxCapacity ?? row.TotalCapacity ?? 0) / 1024.0;
        if (max <= 0) return $"{used:N1} GB";
        return $"{used:N1} / {max:N0} GB";
    }

    public void ReloadFromCatalog()
    {
        try
        {
            var rows = _service.Load();
            var assetRows = _service.LoadAssets();

            // Group assets by env_id once so populating per-env counts is O(1)
            // per env. Comparison is case-insensitive because PPAC env list
            // returns mixed-case GUIDs while the Inventory API normalizes to
            // lower (we already lowercased on ingestion, but stay defensive).
            var assetsByEnv = assetRows
                .Where(a => !string.IsNullOrEmpty(a.EnvId))
                .GroupBy(a => a.EnvId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var r in rows)
            {
                if (!assetsByEnv.TryGetValue(r.EnvId, out var bucket)) continue;

                r.AppCount   = bucket.Count(a => IsAppType(a.AssetType));
                r.FlowCount  = bucket.Count(a => IsFlowType(a.AssetType));
                r.AgentCount = bucket.Count(a => string.Equals(a.AssetType, "agents", StringComparison.OrdinalIgnoreCase));
                // Pre-sort so the expander shows most-recently-touched first.
                r.Assets = bucket
                    .OrderByDescending(a => a.ModifiedUtc ?? a.CreatedUtc ?? DateTime.MinValue)
                    .ToList();

                // NOTE: Solutions / Power Pages / Users are now lazy-loaded
                // from Dataverse on first row expand (see
                // LoadEnvironmentDetailsAsync). We don't pre-populate
                // anything here — the row-details template renders an empty
                // state with a "Load details from Dataverse" hint until the
                // user actually opens a row.
                r.Solutions      = Array.Empty<SolutionGroup>();
                r.PowerPages     = Array.Empty<PowerPageRow>();
                r.UsersAndGroups = Array.Empty<UserGroupRow>();
                r.DetailsLoaded  = false;
                r.DetailsError   = null;
            }

            Environments.Clear();
            foreach (var r in rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
                Environments.Add(r);

            TenantCapacity.Clear();
            foreach (var t in _service.LoadTenantCapacity().OrderBy(t => t.CapacityType, StringComparer.OrdinalIgnoreCase))
                TenantCapacity.Add(t);

            Assets.Clear();
            foreach (var a in assetRows.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
                Assets.Add(a);

            LastRefreshedUtc = _service.LastSyncedUtc();
            StatusText = $"Loaded {Environments.Count} envs + {TenantCapacity.Count} tenant capacity rows + {Assets.Count} assets from local catalog.";
            LastError = null;
            RaiseAggregates();
        }
        catch (Exception ex)
        {
            LastError = $"Catalog load failed: {ex.Message}";
            StatusText = "Local catalog unavailable.";
        }
    }

    private static bool IsAppType(string assetType) => assetType switch
    {
        "canvasapps" or "modeldrivenapps" or "codeapps" or "apps" => true,
        _ => false
    };

    private static bool IsFlowType(string assetType) => assetType switch
    {
        "cloudflows" or "agentflows" or "m365agentflows" => true,
        _ => false
    };

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastError = null;
        StatusText = "Starting refresh...";

        var progress = new Progress<string>(msg => StatusText = msg);
        try
        {
            var result = await Task.Run(() => _service.RefreshAsync(progress)).ConfigureAwait(true);
            ReloadFromCatalog();
            StatusText = $"Refresh complete: {result.EnvironmentCount} envs, " +
                         $"{result.CapacityRows} capacity rows, " +
                         $"{result.AssetRows} assets, " +
                         $"{result.Duration.TotalSeconds:0.0}s.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            LastError = FormatError(ex);
            StatusText = "Refresh failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string FormatError(Exception ex)
    {
        var sb = new StringBuilder();
        // Full exception chain.
        Exception? cur = ex;
        int depth = 0;
        while (cur is not null)
        {
            sb.Append(depth == 0 ? "✖ " : new string(' ', depth * 2) + "⤷ ");
            sb.Append(cur.GetType().FullName).Append(": ").AppendLine(cur.Message);
            cur = cur.InnerException;
            depth++;
        }
        // Captured HTTP failure (if the inventory service exposes one).
        if (_service is PpacInventoryService p && p.LastFailure is { } f)
        {
            sb.AppendLine();
            sb.AppendLine($"HTTP {f.Status} {f.ReasonPhrase}  {f.Method} {f.Url}");
            if (!string.IsNullOrWhiteSpace(f.Body))
            {
                sb.AppendLine("Response body:");
                sb.AppendLine(f.Body);
            }
            sb.AppendLine($"(captured {f.CapturedUtc:O})");
        }
        return sb.ToString().TrimEnd();
    }

    private void OpenTraceLog()
    {
        if (_service is not PpacInventoryService p) return;
        var path = p.TraceLogPath;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LastError = $"Could not open trace log at {path}: {ex.Message}";
        }
    }

    private void CopyError()
    {
        if (string.IsNullOrEmpty(_lastError)) return;
        try
        {
            System.Windows.Clipboard.SetText(_lastError);
            StatusText = $"Copied {_lastError.Length} chars of error text to clipboard.";
        }
        catch (Exception ex)
        {
            // Clipboard can throw if another app holds it; retry once.
            try { System.Windows.Clipboard.SetDataObject(_lastError, copy: true); StatusText = "Copied error text to clipboard."; }
            catch { StatusText = $"Copy failed: {ex.Message}"; }
        }
    }

    private void RaiseAggregates()
    {
        OnPropertyChanged(nameof(EnvironmentCount));
        OnPropertyChanged(nameof(AssetCount));
        OnPropertyChanged(nameof(AssetCountDisplay));
        OnPropertyChanged(nameof(DatabaseGbDisplay));
        OnPropertyChanged(nameof(FileGbDisplay));
        OnPropertyChanged(nameof(LogGbDisplay));
    }

    /// <summary>
    /// Pop the Metadata Inspector dialog for a row. Accepts any of the
    /// drill-down model types (<see cref="SolutionGroup"/>, <see cref="PowerPageRow"/>,
    /// <see cref="UserGroupRow"/>, <see cref="AssetRow"/>) and reads the
    /// pre-captured raw JSON. AssetRow rows synthesize the JSON on-demand
    /// because the Inventory API doesn't store the raw payload (yet).
    /// </summary>
    private void InspectJson(object? param)
    {
        if (param is null) return;
        var (subtitle, raw, makerUrl) = ExtractInspectorPayload(param);
        var win = new MetadataInspectorWindow();
        win.Show(System.Windows.Application.Current?.MainWindow, subtitle, raw, makerUrl);
    }

    /// <summary>Open the row in the maker portal (CommandParameter is the row).</summary>
    private void OpenInMaker(object? param)
    {
        var url = param switch
        {
            EnvironmentRow e => e.MakerUrl,
            SolutionGroup s => s.MakerUrl,
            PowerPageRow  p => p.MakerUrl,
            UserGroupRow  u => u.MakerUrl,
            AssetRow      a => a.MakerUrl,
            _ => null
        };
        if (string.IsNullOrEmpty(url))
        {
            StatusText = "No maker portal URL available for this row.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open maker portal: {ex.Message}";
        }
    }

    private static (string subtitle, string rawJson, string? makerUrl) ExtractInspectorPayload(object row) => row switch
    {
        SolutionGroup s => (
            $"{s.Name} — solution",
            s.RawJson ?? "{ }",
            s.MakerUrl),

        PowerPageRow p => (
            $"{p.Name} — power pages",
            p.RawJson ?? "{ }",
            p.MakerUrl),

        UserGroupRow u => (
            $"{u.DisplayName} — system user",
            u.RawJson ?? "{ }",
            u.MakerUrl),

        AssetRow a => (
            $"{a.DisplayName} — {a.TypeDisplay}",
            // Inventory API row isn't persisted as raw JSON yet; serialize the
            // typed projection so the dialog still has something useful.
            System.Text.Json.JsonSerializer.Serialize(a, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            a.MakerUrl),

        _ => ("(unknown)", System.Text.Json.JsonSerializer.Serialize(row, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), null)
    };

    /// <summary>
    /// Lazy-loads per-env Dataverse details (real solutions, Power Pages,
    /// users) when the user expands a row. Cached on the row instance so
    /// re-selecting the same row is a no-op. Background thread does the HTTP;
    /// property assignments happen on the UI thread because the row is INPC
    /// and the bound DataGrids re-render immediately.
    /// </summary>
    public async Task LoadEnvironmentDetailsAsync(EnvironmentRow row, CancellationToken ct = default)
    {
        if (row is null || row.DetailsLoaded || row.IsLoadingDetails) return;

        row.IsLoadingDetails = true;
        row.DetailsError     = null;

        try
        {
            // Hand the row's cached Inventory-API assets to the client so it
            // can re-bucket them by their owning solution. The client does
            // its own HTTP — Task.Run keeps the UI thread free.
            var details = await Task.Run(
                () => _service.LoadEnvironmentDetailsAsync(row, row.Assets, ct), ct).ConfigureAwait(true);

            row.Solutions      = details.Solutions;
            row.PowerPages     = details.PowerPages;
            row.UsersAndGroups = details.UsersAndGroups;
            row.DetailsLoaded  = true;

            // Enrich the freshly-loaded users with Graph license assignments
            // so the Identity tooltip (and the License column) lights up
            // immediately. The Graph fetch is cached service-side after the
            // first call so subsequent envs reuse the same map for free.
            _ = EnrichUsersWithLicensesAsync(row, ct);
        }
        catch (OperationCanceledException) { /* user navigated away — leave state as-is */ }
        catch (Exception ex)
        {
            row.DetailsError = ex.InnerException?.Message is { Length: > 0 } inner
                ? $"{ex.Message}  ({inner})"
                : ex.Message;
            // Mark as "loaded" so we don't retry on every selection toggle —
            // user can hit the Refresh button at the top to retry the whole tenant.
            row.DetailsLoaded = true;
        }
        finally
        {
            row.IsLoadingDetails = false;
        }
    }

    /// <summary>
    /// One-shot Graph license fetch (cached service-side); then walks the
    /// row's user list and fills in <see cref="UserGroupRow.License"/> +
    /// <see cref="UserGroupRow.LicenseDetails"/> from the UPN→SKU map.
    /// Also resolves owner GUIDs on every cached <see cref="AssetRow"/>
    /// in the env so the Apps/Flows/Agents grids show friendly names
    /// instead of raw GUIDs. Runs fire-and-forget after the Dataverse
    /// user load so the grid pops first and labels light up a moment later.
    /// </summary>
    private async Task EnrichUsersWithLicensesAsync(EnvironmentRow row, CancellationToken ct)
    {
        try
        {
            var graph = await _service.LoadGraphLicensesAsync(ct).ConfigureAwait(true);
            _cachedGraph = graph;
            // Notify the tile/drawer so they pick up the SKU rollup the
            // first time a row enrichment causes the Graph fetch to run.
            OnPropertyChanged(nameof(LicensedUserCount));
            OnPropertyChanged(nameof(LicensedUserCountDisplay));

            foreach (var user in row.UsersAndGroups)
            {
                if (string.IsNullOrEmpty(user.Identity)) continue;
                if (!graph.LicensesByUpn.TryGetValue(user.Identity, out var lics)) continue;
                var (compact, full) = GraphLicenseClient.Format(lics);
                user.License = compact;
                user.LicenseDetails = full;
            }

            // Resolve owner GUIDs on every asset in this env (apps/flows/agents)
            // so both the grouped solution view and the flat view show friendly
            // names. Misses (service principals not enumerated, deleted users)
            // leave OwnerName null and the UI falls back to the raw GUID.
            foreach (var asset in row.Assets)
            {
                if (string.IsNullOrEmpty(asset.OwnerId)) continue;
                if (graph.UserLabelsById.TryGetValue(asset.OwnerId, out var label))
                    asset.OwnerName = label;
            }
        }
        catch (OperationCanceledException) { /* fine */ }
        catch (Exception ex)
        {
            // License enrichment is best-effort — never crash the row.
            StatusText = $"License enrichment skipped: {ex.Message}";
        }
    }

    /// <summary>
    /// Populates the right-side drawer with the tenant license rollup.
    /// First call also kicks off the Graph fetch on the background thread;
    /// subsequent calls reuse the cached SKU map.
    /// </summary>
    private async Task LoadLicenseDrawerAsync()
    {
        try
        {
            var graph = await _service.LoadGraphLicensesAsync().ConfigureAwait(true);
            _cachedGraph = graph;
            OnPropertyChanged(nameof(LicensedUserCount));
            OnPropertyChanged(nameof(LicensedUserCountDisplay));

            DrawerItems.Clear();
            var rollup = graph.GetSkuConsumption();
            if (rollup.Count == 0)
            {
                DrawerItems.Add(new DrawerItem(
                    graph.Warning ?? "No licensed users yet",
                    "If this is unexpected, your account may need User.Read.All / Directory.Read.All in Microsoft Graph.",
                    string.Empty));
                return;
            }

            // Rollup is already sorted descending by user count.
            foreach (var (sku, count) in rollup)
            {
                DrawerItems.Add(new DrawerItem(
                    sku,
                    $"{count:N0} licensed user(s) tenant-wide",
                    count.ToString("N0")));
            }

            DrawerSubtitle = $"{graph.LicensedUserCount:N0} distinct licensed users across {rollup.Count} SKUs " +
                             "(Microsoft Graph subscribedSkus catalog).";
        }
        catch (Exception ex)
        {
            DrawerItems.Add(new DrawerItem(
                "License rollup failed", ex.Message, string.Empty));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _executeAsync = o => { execute(o); return Task.CompletedTask; };
        _canExecute = canExecute;
    }

    public RelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public async void Execute(object? parameter) => await _executeAsync(parameter);

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
