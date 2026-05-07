using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
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
    /// One-shot latch for the security-group name resolution. Independent
    /// of <see cref="_membershipProbeStarted"/> because we want to populate
    /// the env grid's "Security Group" column on every refresh, regardless
    /// of whether the user has flipped the "Only my envs" toggle on.
    /// </summary>
    private bool _groupNamesResolveStarted;

    /// <summary>
    /// Backs the Cancel button on the toolbar. Created fresh per-refresh so a
    /// cancelled refresh doesn't poison the next one. Disposed in
    /// <see cref="RefreshAsync"/>'s finally / next-refresh start.
    /// </summary>
    private CancellationTokenSource? _refreshCts;

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
        CancelRefreshCommand = new RelayCommand(_ => _refreshCts?.Cancel(), _ => IsBusy);
        ReloadCommand = new RelayCommand(_ => ReloadFromCatalog(), _ => !IsBusy);
        OpenTraceLogCommand = new RelayCommand(_ => OpenTraceLog());
        CopyErrorCommand = new RelayCommand(_ => CopyError(), _ => HasError);
        OpenDrawerCommand = new RelayCommand(p => OpenDrawer(p as string));
        CloseDrawerCommand = new RelayCommand(_ => CloseDrawer());
        InspectJsonCommand = new RelayCommand(p => InspectJson(p));
        OpenInMakerCommand = new RelayCommand(p => OpenInMaker(p));
        ShowLicensesCommand = new RelayCommand(p => ShowLicenses(p as UserGroupRow),
                                               p => p is UserGroupRow);
        OpenEnvCommand = new RelayCommand(p => OpenEnv(p as EnvironmentRow));
        CopyTextCommand = new RelayCommand(p => CopyText(p as string));
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        RevokeAdminCommand = new RelayCommand(async p => await RevokeAdminAsync(p as UserGroupRow),
                                              p => p is UserGroupRow u && u.IsAdmin && !IsBusy);
        ClearEnvSearchCommand = new RelayCommand(_ => ResetEnvView(),
                                                 _ => HasEnvSearch || _anyRowExpanded);
        // Per-env "Refresh details" button (lives in each expanded row's
        // detail header). Force-refresh path: drops the SQLite snapshot for
        // that env, hits Dataverse fresh, persists the new snapshot.
        // Disabled while a fetch is in flight to prevent double-clicks.
        RefreshEnvDetailsCommand = new RelayCommand(
            async p => { if (p is EnvironmentRow r) await RefreshEnvironmentDetailsAsync(r); },
            p => p is EnvironmentRow r && !r.IsLoadingDetails);
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

    /// <summary>Cancels the in-flight refresh (if any).</summary>
    public ICommand CancelRefreshCommand { get; }

    /// <summary>
    /// Removes System Administrator from the bound <see cref="UserGroupRow"/>
    /// on its owning env (Dataverse Web API). Modal-confirms before the
    /// DELETE; flips <c>AdminStatus</c> on success so the row's chrome
    /// updates immediately.
    /// </summary>
    public ICommand RevokeAdminCommand { get; }

    /// <summary>Pop the Metadata Inspector dialog showing the row's raw JSON.</summary>
    public ICommand InspectJsonCommand { get; }

    /// <summary>Open the row in the appropriate maker portal in the default browser.</summary>
    public ICommand OpenInMakerCommand { get; }

    /// <summary>
    /// Pop the User Licenses dialog for a <see cref="UserGroupRow"/>. The dialog
    /// renders the Microsoft Graph license assignments enriched onto the row
    /// (<see cref="UserGroupRow.LicenseDetails"/>) as a chip wrap, mirroring
    /// the Metadata Inspector chrome.
    /// </summary>
    public ICommand ShowLicensesCommand { get; }

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

    private string _envSearchText = string.Empty;
    /// <summary>
    /// Free-text filter for the env grid. Matched case-insensitively against
    /// every column the header surfaces (Name, SKU, Status, Region, Version,
    /// Security Group, all capacity displays, Instance URL, Env ID).
    /// Multiple whitespace-separated tokens are AND-ed so the user can type
    /// e.g. <c>"prod uat eastus"</c> to narrow progressively. Empty string
    /// passes everything.
    ///
    /// Setter design notes (UI responsiveness):
    ///   * The TextBox binds with UpdateSourceTrigger=PropertyChanged so the
    ///     watermark / clear-button visibility updates per keystroke.
    ///   * The grid refresh is deferred onto a 200 ms DispatcherTimer so a
    ///     fast typist doesn't pay for N filter passes (each pass walks the
    ///     full env list and re-runs predicates). The keystroke itself is
    ///     never blocked — only the visual filter result is collapsed.
    /// </summary>
    public string EnvSearchText
    {
        get => _envSearchText;
        set
        {
            value ??= string.Empty;
            if (_envSearchText == value) return;
            _envSearchText = value;
            // Cache the lowercased token list once per keystroke so the
            // per-row filter predicate doesn't re-tokenize for every env.
            _envSearchTokens = string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(t => t.ToLowerInvariant())
                       .ToArray();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEnvSearch));
            ScheduleEnvFilterRefresh();
        }
    }
    private string[] _envSearchTokens = Array.Empty<string>();

    /// <summary>True when the env search box has any non-whitespace text — drives the clear-button visibility.</summary>
    public bool HasEnvSearch => _envSearchTokens.Length > 0;

    /// <summary>Clears <see cref="EnvSearchText"/> from the toolbar X button.</summary>
    public ICommand ClearEnvSearchCommand { get; }

    /// <summary>
    /// Per-env force-refresh of the Dataverse drill-down (Solutions / Power
    /// Pages / Users + per-asset enrichments). Bound to the small "Refresh"
    /// button in each expanded row's detail header. Drops the SQLite
    /// snapshot for that env, then hits Dataverse fresh and re-persists.
    /// Command parameter is the <see cref="EnvironmentRow"/> to refresh.
    /// </summary>
    public ICommand RefreshEnvDetailsCommand { get; }

    /// <summary>
    /// Single "reset env view" entry-point fired by the search-box X button.
    /// Three responsibilities, in this order:
    ///   1. SYNCHRONOUS — wipe <see cref="EnvSearchText"/> + raise INPC so
    ///      the TextBox, X-button visibility and watermark all repaint on
    ///      the very next layout pass. The user gets the visual confirmation
    ///      that their click was heard before any heavy work runs.
    ///   2. ASYNC (Background priority) — collapse every expanded env so
    ///      focus-mode releases and WPF tears down the heavy per-env detail
    ///      visuals (six PagedDataGrids per expanded row, ~hundreds of items
    ///      each). This was the actual blocking cost on the previous version
    ///      because we did it inline with the click; deferring to Background
    ///      lets the UI thread breathe between collapse + grid re-virtualization.
    ///   3. ASYNC (Background priority, after step 2) — final
    ///      <see cref="ICollectionView.Refresh"/> so the grid re-runs the
    ///      filter predicate and snaps back to "all envs".
    /// </summary>
    private void ResetEnvView()
    {
        // Cancel any debounced refresh queued by the last keystroke; we
        // own the next refresh below.
        _envFilterDebounce?.Stop();

        // STEP 1 — synchronous, instantaneous visual feedback.
        // Wipe state directly so we don't pay the EnvSearchText setter's
        // debounce. INPC keeps the bound TextBox / clear button / watermark
        // in sync on the very next render tick.
        bool hadSearch = _envSearchText.Length > 0 || _envSearchTokens.Length > 0;
        if (hadSearch)
        {
            _envSearchText = string.Empty;
            _envSearchTokens = Array.Empty<string>();
            OnPropertyChanged(nameof(EnvSearchText));
            OnPropertyChanged(nameof(HasEnvSearch));
        }

        // STEPS 2 + 3 — defer the heavy work. WPF needs to dispose the
        // expanded row's RowDetailsTemplate visual tree (6 PagedDataGrids
        // with potentially thousands of items + filter funnels), then
        // re-virtualize all envs. Doing that inline with the click made
        // the click feel "stuck" \u2014 the Window message pump didn't get a
        // turn to repaint the search box. DispatcherPriority.Background
        // schedules behind input + render so the cleared TextBox paints
        // first; the user perceives "instant clear, list re-populates".
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            // Test / headless fallback \u2014 just do the work synchronously.
            CollapseAllRowsAndRefresh();
            return;
        }
        dispatcher.BeginInvoke(
            new Action(CollapseAllRowsAndRefresh),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Heavy-half of <see cref="ResetEnvView"/>: collapses every expanded
    /// env (releases focus mode and tears down per-env detail visuals)
    /// and runs one final filter refresh. Always invoked on the UI thread.
    /// Idempotent so it's safe even if no row was actually expanded.
    /// </summary>
    private void CollapseAllRowsAndRefresh()
    {
        // Suppress the per-row Refresh that RecomputeAnyRowExpanded would
        // otherwise trigger when the last expanded row flips to false. We
        // do exactly one Refresh below, so doubling up just doubles the
        // wall-time cost on a 716-row grid.
        _suppressExpansionRefresh = true;
        try
        {
            foreach (var row in Environments)
                if (row.IsExpanded) row.IsExpanded = false;
        }
        finally
        {
            _suppressExpansionRefresh = false;
        }
        // Recompute the cached flag now that all rows have flipped, then
        // run the single canonical refresh.
        _anyRowExpanded = false;
        EnvironmentsView.Refresh();
    }

    /// <summary>
    /// Set true while <see cref="CollapseAllRowsAndRefresh"/> bulk-collapses
    /// rows so the per-row INPC handler doesn't trigger N intermediate
    /// CollectionView refreshes.
    /// </summary>
    private bool _suppressExpansionRefresh;

    // Debounce machinery for the env search box. A single DispatcherTimer
    // is reset on every keystroke; it fires once 200ms after the LAST
    // character so a fast typist sees the grid refresh exactly once.
    private System.Windows.Threading.DispatcherTimer? _envFilterDebounce;
    private void ScheduleEnvFilterRefresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            // Fallback for unit-test contexts without an Application instance.
            EnvironmentsView.Refresh();
            return;
        }
        if (_envFilterDebounce == null)
        {
            _envFilterDebounce = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _envFilterDebounce.Tick += (_, _) =>
            {
                _envFilterDebounce!.Stop();
                EnvironmentsView.Refresh();
            };
        }
        _envFilterDebounce.Stop();
        _envFilterDebounce.Start();
    }

    // -----------------------------------------------------------------------
    // Per-column filters (Sentinel-style funnel popups in each header). Each
    // setter triggers EnvironmentsView.Refresh so the grid live-filters as
    // the user types. Empty string means "no filter on this column" — checked
    // upfront in FilterEnvironment so filter-free columns short-circuit out.
    // Hosted XAML in Inventory/ColumnFilterHeader.xaml; bindings use
    // RelativeSource AncestorType=DataGrid because DataGrid column headers
    // are not in the visual tree (so a plain {Binding FilterName} won't
    // resolve the VM).
    // -----------------------------------------------------------------------
    private string _filterName = string.Empty;
    private string _filterSku = string.Empty;
    private string _filterStatus = string.Empty;
    private string _filterRegion = string.Empty;
    private string _filterVersion = string.Empty;
    private string _filterCreated = string.Empty;
    private string _filterSecurityGroup = string.Empty;
    private string _filterDatabase = string.Empty;
    private string _filterFile = string.Empty;
    private string _filterLog = string.Empty;
    private string _filterFinOpsDb = string.Empty;
    private string _filterFinOpsFile = string.Empty;
    private string _filterInstanceUrl = string.Empty;

    /// <summary>Per-column filter — Name. Substring match, case-insensitive.</summary>
    public string FilterName        { get => _filterName;          set => SetColumnFilter(ref _filterName,        value); }
    public string FilterSku         { get => _filterSku;           set => SetColumnFilter(ref _filterSku,         value); }
    public string FilterStatus      { get => _filterStatus;        set => SetColumnFilter(ref _filterStatus,      value); }
    public string FilterRegion      { get => _filterRegion;        set => SetColumnFilter(ref _filterRegion,      value); }
    public string FilterVersion     { get => _filterVersion;       set => SetColumnFilter(ref _filterVersion,     value); }
    public string FilterCreated     { get => _filterCreated;       set => SetColumnFilter(ref _filterCreated,     value); }
    public string FilterSecurityGroup { get => _filterSecurityGroup; set => SetColumnFilter(ref _filterSecurityGroup, value); }
    public string FilterDatabase    { get => _filterDatabase;      set => SetColumnFilter(ref _filterDatabase,    value); }
    public string FilterFile        { get => _filterFile;          set => SetColumnFilter(ref _filterFile,        value); }
    public string FilterLog         { get => _filterLog;           set => SetColumnFilter(ref _filterLog,         value); }
    public string FilterFinOpsDb    { get => _filterFinOpsDb;      set => SetColumnFilter(ref _filterFinOpsDb,    value); }
    public string FilterFinOpsFile  { get => _filterFinOpsFile;    set => SetColumnFilter(ref _filterFinOpsFile,  value); }
    public string FilterInstanceUrl { get => _filterInstanceUrl;   set => SetColumnFilter(ref _filterInstanceUrl, value); }

    /// <summary>
    /// True if any per-column filter is non-empty. Used to drive the
    /// "Clear column filters" toolbar button visibility.
    /// </summary>
    public bool HasAnyColumnFilter =>
        !string.IsNullOrWhiteSpace(_filterName) ||
        !string.IsNullOrWhiteSpace(_filterSku) ||
        !string.IsNullOrWhiteSpace(_filterStatus) ||
        !string.IsNullOrWhiteSpace(_filterRegion) ||
        !string.IsNullOrWhiteSpace(_filterVersion) ||
        !string.IsNullOrWhiteSpace(_filterCreated) ||
        !string.IsNullOrWhiteSpace(_filterSecurityGroup) ||
        !string.IsNullOrWhiteSpace(_filterDatabase) ||
        !string.IsNullOrWhiteSpace(_filterFile) ||
        !string.IsNullOrWhiteSpace(_filterLog) ||
        !string.IsNullOrWhiteSpace(_filterFinOpsDb) ||
        !string.IsNullOrWhiteSpace(_filterFinOpsFile) ||
        !string.IsNullOrWhiteSpace(_filterInstanceUrl);

    /// <summary>
    /// Helper used by every per-column filter setter — normalises null→"",
    /// short-circuits no-op assignments, raises INPC for the property +
    /// HasAnyColumnFilter, then refreshes the env CollectionView.
    /// </summary>
    private void SetColumnFilter(ref string field, string? value, [CallerMemberName] string? name = null)
    {
        value ??= string.Empty;
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(HasAnyColumnFilter));
        EnvironmentsView.Refresh();
    }

    /// <summary>
    /// Clear every per-column env-grid filter at once. Wired to a "Clear
    /// column filters" button on the toolbar that only appears when at least
    /// one filter is set (HasAnyColumnFilter).
    /// </summary>
    public ICommand ClearColumnFiltersCommand => _clearColumnFiltersCommand ??= new RelayCommand(_ =>
    {
        _filterName = _filterSku = _filterStatus = _filterRegion = _filterVersion =
            _filterCreated = _filterSecurityGroup = _filterDatabase = _filterFile =
            _filterLog = _filterFinOpsDb = _filterFinOpsFile = _filterInstanceUrl = string.Empty;
        OnPropertyChanged(nameof(FilterName)); OnPropertyChanged(nameof(FilterSku));
        OnPropertyChanged(nameof(FilterStatus)); OnPropertyChanged(nameof(FilterRegion));
        OnPropertyChanged(nameof(FilterVersion)); OnPropertyChanged(nameof(FilterCreated));
        OnPropertyChanged(nameof(FilterSecurityGroup)); OnPropertyChanged(nameof(FilterDatabase));
        OnPropertyChanged(nameof(FilterFile)); OnPropertyChanged(nameof(FilterLog));
        OnPropertyChanged(nameof(FilterFinOpsDb)); OnPropertyChanged(nameof(FilterFinOpsFile));
        OnPropertyChanged(nameof(FilterInstanceUrl));
        OnPropertyChanged(nameof(HasAnyColumnFilter));
        EnvironmentsView.Refresh();
    }, _ => HasAnyColumnFilter);
    private RelayCommand? _clearColumnFiltersCommand;

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
        // Skip the refresh while CollapseAllRowsAndRefresh is bulk-flipping
        // rows; it owns the single canonical refresh after the loop.
        if (_suppressExpansionRefresh) return;
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

        // Per-column filters (Sentinel-style header funnels). Each is an
        // independent substring match on the column's display value; ANDed
        // together so multiple filters narrow progressively. Empty string
        // short-circuits — most columns have no filter on most refreshes,
        // so this is essentially free in the common case.
        if (!ColumnContains(_filterName,           row.DisplayName)) return false;
        if (!ColumnContains(_filterSku,            row.Sku)) return false;
        if (!ColumnContains(_filterStatus,         row.ProvisioningState)) return false;
        if (!ColumnContains(_filterRegion,         row.Region)) return false;
        if (!ColumnContains(_filterVersion,        row.Version)) return false;
        if (!ColumnContains(_filterCreated,        row.CreatedUtc?.ToString("yyyy-MM-dd"))) return false;
        if (!ColumnContains(_filterSecurityGroup,  row.SecurityGroupSummary)) return false;
        if (!ColumnContains(_filterDatabase,       row.DatabaseGbDisplay)) return false;
        if (!ColumnContains(_filterFile,           row.FileGbDisplay)) return false;
        if (!ColumnContains(_filterLog,            row.LogGbDisplay)) return false;
        if (!ColumnContains(_filterFinOpsDb,       row.FinOpsDatabaseGbDisplay)) return false;
        if (!ColumnContains(_filterFinOpsFile,     row.FinOpsFileGbDisplay)) return false;
        if (!ColumnContains(_filterInstanceUrl,    row.InstanceUrl)) return false;

        // Free-text search across every column the env grid header surfaces.
        // Tokens are AND-ed: each whitespace-separated token must appear in
        // at least one column. Done last so the cheaper toggles short-circuit
        // first when the user is just toggling membership / focus mode.
        if (_envSearchTokens.Length > 0)
        {
            // Single concatenated haystack so we don't allocate per-token
            // per-column. Includes everything visible in the header row plus
            // ids that are useful for paste-and-find debugging.
            var haystack = string.Join('\u001f', new[]
            {
                row.DisplayName,
                row.Sku,
                row.ProvisioningState,
                row.Region,
                row.Version,
                row.IsDefault ? "default" : null,
                row.IsManagedEnvironment ? "managed" : null,
                row.CreatedUtc?.ToString("yyyy-MM-dd"),
                row.SecurityGroupSummary,
                row.SecurityGroupId,
                row.DatabaseGbDisplay,
                row.FileGbDisplay,
                row.LogGbDisplay,
                row.FinOpsDatabaseGbDisplay,
                row.FinOpsFileGbDisplay,
                row.InstanceUrl,
                row.EnvId
            }.Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();

            for (int i = 0; i < _envSearchTokens.Length; i++)
            {
                if (haystack.IndexOf(_envSearchTokens[i], StringComparison.Ordinal) < 0)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Per-column-filter helper: empty filter passes; otherwise case-insensitive
    /// substring match against the cell's display value. Null / empty cell text
    /// fails any non-empty filter (so the user gets only rows where the column
    /// has a value matching the typed substring).
    /// </summary>
    private static bool ColumnContains(string filter, string? cellValue)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (string.IsNullOrEmpty(cellValue)) return false;
        return cellValue.Contains(filter, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Resolves AAD security-group display names via Microsoft Graph and
    /// stamps each <see cref="EnvironmentRow.SecurityGroupDisplayName"/>.
    /// Runs at most once per session — independent of the "Only my envs"
    /// toggle so the env grid's "Security Group" column always shows
    /// human-readable names. Silent on failure (the column falls back to
    /// the GUID prefix automatically).
    /// </summary>
    private async Task EnsureSecurityGroupNamesAsync()
    {
        if (_groupNamesResolveStarted) return;
        _groupNamesResolveStarted = true;
        try
        {
            var groupIds = Environments
                .Select(e => e.SecurityGroupId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (groupIds.Count == 0) return;

            var names = await _service.ResolveSecurityGroupNamesAsync(groupIds).ConfigureAwait(false);
            if (names.Count == 0) return;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var row in Environments)
                {
                    if (string.IsNullOrEmpty(row.SecurityGroupId)) continue;
                    if (names.TryGetValue(row.SecurityGroupId, out var name) && !string.IsNullOrEmpty(name))
                        row.SecurityGroupDisplayName = name;
                }
            });
        }
        catch
        {
            // Best-effort. Allow a future retry on next refresh.
            _groupNamesResolveStarted = false;
        }
    }

    /// <summary>
    /// Yes/No-confirmed Revoke System Admin call against the Users sub-grid.
    /// Looks up the System Administrator role on the env and disassociates
    /// it from the row's <see cref="UserGroupRow.SystemUserId"/>. On 204
    /// flips the row's <c>AdminStatus</c> so the grid + button visibility
    /// re-render immediately. On failure surfaces the response body in
    /// <see cref="LastError"/> so the admin can act on the error.
    /// </summary>
    private async Task RevokeAdminAsync(UserGroupRow? user)
    {
        if (user is null) { StatusText = "Revoke Admin: no user selected."; return; }
        if (string.IsNullOrEmpty(user.SystemUserId) || string.IsNullOrEmpty(user.InstanceUrl))
        {
            StatusText = "Revoke Admin: row is missing systemuserid or instance URL.";
            return;
        }
        if (!user.IsAdmin)
        {
            StatusText = $"Revoke Admin: {user.DisplayName} is not currently an admin.";
            return;
        }

        // Resolve a friendly env label so the confirm dialog reads "on
        // environment Production" instead of dumping the GUID.
        var envLabel = !string.IsNullOrEmpty(user.EnvId)
            ? Environments.FirstOrDefault(e => string.Equals(e.EnvId, user.EnvId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? user.EnvId
            : "(unknown env)";

        // Use the WPF-UI Fluent MessageBox so the confirmation matches the
        // rest of the app's chrome (Mica title-bar, themed buttons, accent
        // primary). The Win32 MessageBox.Show() looks alien against the
        // FluentWindow shell.
        var msg = new Wpf.Ui.Controls.MessageBox
        {
            Title           = "Revoke System Administrator?",
            Content         =
                $"Remove the System Administrator role from\n" +
                $"  {user.DisplayName}  ({user.Identity})\n\n" +
                $"on environment \"{envLabel}\"?\n\n" +
                "This calls Dataverse Web API and is auditable. " +
                "You must hold System Administrator on the target env.",
            PrimaryButtonText        = "Yes, revoke",
            PrimaryButtonAppearance  = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText          = "Cancel",
            CloseButtonAppearance    = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Owner                    = System.Windows.Application.Current?.MainWindow
        };
        var dialogResult = await msg.ShowDialogAsync().ConfigureAwait(true);
        if (dialogResult != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        try
        {
            StatusText = $"Revoking admin for {user.DisplayName}...";
            await _service.RevokeSystemAdminAsync(user.InstanceUrl!, user.SystemUserId!).ConfigureAwait(true);
            user.AdminStatus = "Non-Admin";
            StatusText = $"Revoked System Administrator from {user.DisplayName}.";
        }
        catch (Exception ex)
        {
            LastError = $"Revoke admin failed for {user.DisplayName}: {ex.Message}";
            StatusText = "Revoke admin failed.";
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
                    var subtitle = $"DB: {row.DatabaseGbDisplay}   File: {row.FileGbDisplay}   Log: {row.LogGbDisplay}";
                    if (row.HasFinOps)
                    {
                        // FinOps-licensed envs (Dynamics 365 F&O) have a
                        // separate accounting bucket; tack it on so admins
                        // see Dataverse + FinOps side-by-side.
                        subtitle += $"   FinOps DB: {row.FinOpsDatabaseGbDisplay}   FinOps File: {row.FinOpsFileGbDisplay}";
                    }
                    var badge = row.DatabaseStatus switch
                    {
                        "over" => "OVER",
                        "warn" => "NEAR",
                        _      => row.Sku ?? string.Empty
                    };
                    DrawerItems.Add(new DrawerItem(row.DisplayName ?? row.EnvId, subtitle, badge));
                }
                if (DrawerItems.Count == 0)
                    DrawerItems.Add(new DrawerItem("No capacity data yet", "Click Refresh to pull from BAP.", string.Empty));
                break;

            case "governance":
                DrawerTitle    = "Tenant Governance: DLP Policies";
                DrawerSubtitle = "Loading DLP policies from BAP " +
                                 "(/providers/PowerPlatform.Governance/v2/policies)…";
                _ = LoadDlpDrawerAsync();
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
            (CancelRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

            // Fire-and-forget: backfill the env grid's Security Group column
            // with display names from Microsoft Graph. Independent of the
            // "Only my envs" toggle (which kicks the membership probe). Safe
            // to call repeatedly — internal latch ensures the Graph call
            // happens at most once per session.
            _ = EnsureSecurityGroupNamesAsync();
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

        // Fresh CTS for this refresh; CancelRefreshCommand fires .Cancel().
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        (CancelRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();

        var progress = new Progress<string>(msg => StatusText = msg);

        // Per-phase callback: as soon as a phase lands in SQLite, marshal to
        // the UI thread and rebuild the bound collections so the user sees
        // env grid + tiles populate incrementally instead of waiting for the
        // slowest phase (Inventory API) to finish.
        Func<RefreshPhase, Task> onPhase = phase =>
        {
            return Application.Current?.Dispatcher is { } d
                ? d.InvokeAsync(() => ReloadFromCatalog()).Task
                : Task.CompletedTask;
        };

        try
        {
            var result = await Task.Run(() => _service.RefreshAsync(progress, onPhase, ct), ct).ConfigureAwait(true);
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
            (CancelRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    /// <summary>
    /// Pop the <see cref="UserLicensesWindow"/> for the given user row. Source
    /// data is whatever <c>GraphLicenseClient</c> already enriched onto the
    /// row — no additional fetch is performed here so the dialog opens
    /// instantly. If Graph hasn't enriched the row yet (admin Graph perms
    /// missing, or a brand-new env that hasn't been expanded), the dialog
    /// shows its empty state with that explanation.
    /// </summary>
    private void ShowLicenses(UserGroupRow? row)
    {
        if (row is null) return;
        var win = new UserLicensesWindow();
        win.Show(System.Windows.Application.Current?.MainWindow, row);
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
    /// <para>
    /// Caching policy: the FIRST expansion ever (or after the user clicks
    /// the per-env Refresh button) hits Dataverse and persists a snapshot
    /// to local SQLite. Every subsequent expansion — including across app
    /// launches — hydrates synchronously from SQLite, no HTTP at all. The
    /// per-env Refresh button calls back here with
    /// <paramref name="forceRefresh"/> = <c>true</c> to bypass the cache.
    /// </para>
    /// </summary>
    public async Task LoadEnvironmentDetailsAsync(
        EnvironmentRow row,
        CancellationToken ct = default,
        bool forceRefresh = false)
    {
        if (row is null) return;
        if (row.IsLoadingDetails) return;
        // Cached or already-loaded? Skip the network unless the user explicitly
        // asked for a refresh. DetailsLoaded gates re-runs in the same session;
        // forceRefresh resets it before re-fetching below.
        if (row.DetailsLoaded && !forceRefresh) return;

        row.IsLoadingDetails = true;
        row.DetailsError     = null;

        try
        {
            // Hand the row's cached Inventory-API assets to the client so it
            // can re-bucket them by their owning solution. The client does
            // its own HTTP (or hydrates the cached snapshot synchronously)
            // — Task.Run keeps the UI thread free either way.
            var details = await Task.Run(
                () => _service.LoadEnvironmentDetailsAsync(row, row.Assets, forceRefresh, ct), ct).ConfigureAwait(true);

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
    /// User-initiated re-fetch of one env's Dataverse drill-down. Bound to
    /// the per-env "Refresh" button on the row's detail header. Resets the
    /// row's loaded flag so <see cref="LoadEnvironmentDetailsAsync"/> with
    /// <c>forceRefresh: true</c> actually does work — otherwise it would
    /// short-circuit on the cached <c>DetailsLoaded == true</c>.
    /// </summary>
    public async Task RefreshEnvironmentDetailsAsync(EnvironmentRow row, CancellationToken ct = default)
    {
        if (row is null || row.IsLoadingDetails) return;
        row.DetailsLoaded = false;
        await LoadEnvironmentDetailsAsync(row, ct, forceRefresh: true).ConfigureAwait(true);
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

    /// <summary>
    /// Populates the right-side drawer with the tenant DLP policy list. First
    /// call also kicks off the BAP fetch on the background thread; subsequent
    /// calls reuse the cached snapshot owned by the inventory service.
    /// One row per policy, with subtitle showing scope + classified-connector
    /// counts (Business / Non-Business / Blocked) and the badge showing the
    /// scope kind (ALL / ONLY / EXCEPT) so admins can see at a glance whether
    /// a policy is global or env-scoped.
    /// </summary>
    private async Task LoadDlpDrawerAsync()
    {
        try
        {
            var policies = await _service.LoadDlpPoliciesAsync().ConfigureAwait(true);

            DrawerItems.Clear();
            if (policies.Count == 0)
            {
                DrawerSubtitle = "No DLP policies configured on this tenant.";
                DrawerItems.Add(new DrawerItem(
                    "No DLP policies",
                    "Create a policy in the Power Platform admin center to control which connectors can be combined.",
                    string.Empty));
                return;
            }

            // Scope kind drives the badge — admins want to see at-a-glance
            // which policies are tenant-wide vs env-scoped.
            foreach (var p in policies.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var businessCount  = CountConnectors(p, "Confidential");
                var generalCount   = CountConnectors(p, "General");
                var blockedCount   = CountConnectors(p, "Blocked");
                var envCount       = p.Environments?.Count ?? 0;

                var scopeText = (p.EnvironmentType ?? "AllEnvironments") switch
                {
                    "AllEnvironments"    => "Scope: All environments",
                    "OnlyEnvironments"   => $"Scope: Only {envCount} env(s)",
                    "ExceptEnvironments" => $"Scope: All except {envCount} env(s)",
                    _                    => $"Scope: {p.EnvironmentType}"
                };
                var ruleText  = $"Business: {businessCount}   Non-Business: {generalCount}   Blocked: {blockedCount}";
                var createdBy = p.CreatedBy?.DisplayName;
                var subtitle  = string.IsNullOrEmpty(createdBy)
                    ? $"{scopeText}   •   {ruleText}"
                    : $"{scopeText}   •   {ruleText}   •   Created by: {createdBy}";

                var badge = (p.EnvironmentType ?? "AllEnvironments") switch
                {
                    "AllEnvironments"    => "ALL",
                    "OnlyEnvironments"   => "ONLY",
                    "ExceptEnvironments" => "EXCEPT",
                    _                    => "POLICY"
                };

                DrawerItems.Add(new DrawerItem(
                    p.DisplayName ?? p.Name ?? "(unnamed policy)",
                    subtitle,
                    badge));
            }

            DrawerSubtitle = $"{policies.Count:N0} DLP policy(ies) tenant-wide " +
                             "(BAP /providers/PowerPlatform.Governance/v2/policies).";
        }
        catch (Exception ex)
        {
            DrawerItems.Clear();
            DrawerSubtitle = "DLP policy fetch failed.";
            DrawerItems.Add(new DrawerItem(
                "DLP policy fetch failed",
                ex.Message + "  (Need Power Platform Admin / Service Admin role to read policies.)",
                string.Empty));
        }
    }

    /// <summary>
    /// Sum the connector counts across every group whose classification
    /// matches the supplied bucket name. The v2 envelope returns groups in
    /// no particular order so we can't index by position; classification
    /// strings are stable: <c>Confidential</c> = Business, <c>General</c> =
    /// Non-Business, <c>Blocked</c> = Blocked.
    /// </summary>
    private static int CountConnectors(BapDlpClient.DlpPolicyDto p, string classification)
    {
        if (p.ConnectorGroups is null) return 0;
        var n = 0;
        foreach (var g in p.ConnectorGroups)
        {
            if (!string.Equals(g.Classification, classification, StringComparison.OrdinalIgnoreCase)) continue;
            n += g.Connectors?.Count ?? 0;
        }
        return n;
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

    // Hook WPF's global CommandManager.RequerySuggested so any UI input event
    // (focus change, mouse move into a control, key press) triggers a fresh
    // CanExecute evaluation. Without this, ICommand bindings that depend on
    // properties WPF can't see (e.g. row.IsLoadingDetails on a per-row
    // RowDetailsTemplate-bound RelayCommand, or CommandParameter bindings
    // that propagate after the first CanExecute call) stay stuck in their
    // initial state — which is why per-env Refresh buttons rendered
    // permanently disabled even when IsLoadingDetails was false.
    //
    // We still expose RaiseCanExecuteChanged() for explicit pushes, and
    // chain it through CommandManager.InvalidateRequerySuggested so all
    // bound buttons re-query on the same tick.
    public event EventHandler? CanExecuteChanged
    {
        add    { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
    public void RaiseCanExecuteChanged()
        => CommandManager.InvalidateRequerySuggested();
}
