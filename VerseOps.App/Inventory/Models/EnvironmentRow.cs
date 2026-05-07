using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VerseOps.App.Inventory.Models;

/// <summary>
/// One row in the inventory grid; mirrors the columns the PCF dashboard shows
/// (Name | SKU | Status | Region | Ver | Default | Created | URL | DB | Files | Log).
/// All values are flattened from the PPAC SDK Environment response.
///
/// Implements <see cref="INotifyPropertyChanged"/> so the lazy-loaded per-env
/// detail collections (<see cref="Solutions"/>, <see cref="PowerPages"/>,
/// <see cref="UsersAndGroups"/>) and their loading state can update the
/// row-details expander while the user has the row open.
/// </summary>
public sealed class EnvironmentRow : INotifyPropertyChanged
{
    public required string EnvId { get; init; }
    public string? DisplayName { get; set; }
    public string? Sku { get; set; }
    public string? Region { get; set; }
    public string? ProvisioningState { get; set; }
    public string? Version { get; set; }
    public string? InstanceUrl { get; set; }
    public bool IsDefault { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public DateTime LastSyncedUtc { get; set; }

    /// <summary>
    /// Azure AD security group ID gating Dataverse user access. PPAC env
    /// metadata exposes this in <c>AdditionalData["securityGroupId"]</c>.
    /// Empty / null when the env has no security group (open to anyone in
    /// the tenant — typical for the default env). Used by the "Only my
    /// environments" toggle: we hide envs without a group, and within the
    /// remainder we keep only those whose group the signed-in user is a
    /// member of (verified via Microsoft Graph).
    /// </summary>
    public string? SecurityGroupId { get; set; }

    /// <summary>
    /// Display name of the AAD security group, resolved lazily via
    /// Microsoft Graph <c>POST /directoryObjects/getByIds</c> after the
    /// env list lands. INPC because the resolution races with grid render —
    /// rows are bound first, then names backfill in. Null when the env
    /// has no security group; non-null but empty after a Graph error.
    /// </summary>
    private string? _securityGroupDisplayName;
    public string? SecurityGroupDisplayName
    {
        get => _securityGroupDisplayName;
        set { _securityGroupDisplayName = value; OnPropertyChanged(); OnPropertyChanged(nameof(SecurityGroupSummary)); }
    }

    /// <summary>
    /// User-facing single-cell summary for the env grid's "Security Group"
    /// column. Combines name + membership badge + open-to-all marker so the
    /// admin can see at a glance who controls access to the env. Order of
    /// precedence:
    ///   1. No group → "Open to all" (default env / no gating).
    ///   2. Group with name + member → "Group Name ★".
    ///   3. Group with name only → "Group Name".
    ///   4. Group ID only (Graph not yet resolved or failed) → first 8 chars
    ///      of the GUID + "…" so the cell shows *something* and a tooltip
    ///      can carry the full id.
    /// </summary>
    public string SecurityGroupSummary
    {
        get
        {
            if (string.IsNullOrEmpty(SecurityGroupId)) return "Open to all";
            var name = string.IsNullOrEmpty(_securityGroupDisplayName)
                ? SecurityGroupId.Substring(0, Math.Min(8, SecurityGroupId.Length)) + "…"
                : _securityGroupDisplayName!;
            return _isCurrentUserInSecurityGroup == true ? $"{name} ★" : name;
        }
    }

    /// <summary>
    /// True when the env has the Power Platform "Managed Environments"
    /// premium governance turned on. Derived from PPAC <c>ProtectionLevel</c>:
    /// <c>"Standard"</c> = Managed, anything else (typically <c>"Basic"</c>)
    /// = unmanaged. Surfaced as a column / badge so admins can spot which
    /// envs already have premium governance applied.
    /// </summary>
    public bool IsManagedEnvironment { get; set; }

    // Capacity values (top-line) from gov_capacity, joined for grid rendering.
    // Both the Actual (consumed) and the Rated (limit) sides are read so the
    // grid can render "12.4 / 100.0 GB" with an over-limit indicator.
    public double? DatabaseGb { get; set; }
    public double? FileGb { get; set; }
    public double? LogGb { get; set; }
    public double? DatabaseLimitGb { get; set; }
    public double? FileLimitGb { get; set; }
    public double? LogLimitGb { get; set; }

    // FinOps-side capacity (Dynamics 365 Finance & Operations envs only —
    // null on standard Dataverse envs). BAP returns these as the
    // "FinOpsDatabase" / "FinOpsFile" capacity types.
    public double? FinOpsDatabaseGb { get; set; }
    public double? FinOpsFileGb { get; set; }
    public double? FinOpsDatabaseLimitGb { get; set; }
    public double? FinOpsFileLimitGb { get; set; }

    /// <summary>True if any FinOps capacity row was returned for this env.</summary>
    public bool HasFinOps =>
        (FinOpsDatabaseGb ?? 0) > 0 || (FinOpsFileGb ?? 0) > 0 ||
        (FinOpsDatabaseLimitGb ?? 0) > 0 || (FinOpsFileLimitGb ?? 0) > 0;

    // ------------------------------------------------------------------
    // Display formatters used by the env grid. Each returns "12.4 / 100 GB"
    // when both sides are known, "12.4 GB" when only consumption is known,
    // and "—" when no data at all. Paired with a *Status property below
    // that drives the cell background colour (ok/warn/over).
    // ------------------------------------------------------------------
    public string DatabaseGbDisplay      => FormatGb(DatabaseGb,      DatabaseLimitGb);
    public string FileGbDisplay          => FormatGb(FileGb,          FileLimitGb);
    public string LogGbDisplay           => FormatGb(LogGb,           LogLimitGb,      logUnits: true);
    public string FinOpsDatabaseGbDisplay => FormatGb(FinOpsDatabaseGb, FinOpsDatabaseLimitGb);
    public string FinOpsFileGbDisplay     => FormatGb(FinOpsFileGb,     FinOpsFileLimitGb);

    /// <summary>"ok" / "warn" / "over" / "" — drives the cell foreground/background colour via DataTriggers.</summary>
    public string DatabaseStatus       => UsageStatus(DatabaseGb,       DatabaseLimitGb);
    public string FileStatus           => UsageStatus(FileGb,           FileLimitGb);
    public string LogStatus            => UsageStatus(LogGb,            LogLimitGb);
    public string FinOpsDatabaseStatus => UsageStatus(FinOpsDatabaseGb, FinOpsDatabaseLimitGb);
    public string FinOpsFileStatus     => UsageStatus(FinOpsFileGb,     FinOpsFileLimitGb);

    private static string FormatGb(double? actual, double? limit, bool logUnits = false)
    {
        if (!actual.HasValue && !limit.HasValue) return "—";
        var fmt = logUnits ? "N3" : "N2";
        if (actual.HasValue && limit.HasValue && limit.Value > 0)
            return $"{actual.Value.ToString(fmt)} / {limit.Value:N0} GB";
        if (actual.HasValue) return $"{actual.Value.ToString(fmt)} GB";
        return $"limit {limit!.Value:N0} GB";
    }

    private static string UsageStatus(double? actual, double? limit)
    {
        if (!actual.HasValue || !limit.HasValue || limit.Value <= 0) return "";
        var ratio = actual.Value / limit.Value;
        if (ratio >= 1.0)  return "over";   // hard breach — red.
        if (ratio >= 0.80) return "warn";   // approaching — amber.
        return "ok";
    }

    // Asset counts joined in from gov_asset (Power Platform Inventory API).
    // Populated by the view-model after the catalog is loaded so the env grid
    // can show per-env Apps/Flows/Agents columns alongside capacity.
    public int AppCount { get; set; }
    public int FlowCount { get; set; }
    public int AgentCount { get; set; }

    /// <summary>
    /// Per-env asset list bound to the DataGrid <c>RowDetailsTemplate</c>.
    /// Populated by <see cref="VerseOps.App.Inventory.InventoryViewModel.ReloadFromCatalog"/>
    /// after the SQLite cache is read so the row expander renders instantly
    /// from memory (no extra HTTP round-trip when a row is selected).
    /// </summary>
    public IReadOnlyList<AssetRow> Assets { get; set; } = Array.Empty<AssetRow>();

    // ------------------------------------------------------------------
    // Flat per-type slices over Assets — used by the "Flat" view-mode in
    // the row-details template (alternative to grouping by solution).
    // Computed lazily from the cached Assets collection so they stay in
    // sync without an explicit refresh.
    // ------------------------------------------------------------------
    public IEnumerable<AssetRow> AllApps   => Assets.Where(a =>
        a.AssetType is "canvasapps" or "modeldrivenapps" or "codeapps" or "apps");
    public IEnumerable<AssetRow> AllFlows  => Assets.Where(a =>
        a.AssetType is "cloudflows" or "agentflows" or "m365agentflows");
    public IEnumerable<AssetRow> AllAgents => Assets.Where(a =>
        string.Equals(a.AssetType, "agents", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // Row-details view mode toggle. The user can choose between:
    //   • Solutions view — assets grouped under their owning Dataverse
    //     solution (Expander rows). Default.
    //   • Flat view — three siblling grids (Apps / Flows / Agents) showing
    //     every asset in the env, ungrouped, plus Power Pages and Users.
    // The two flags are mutually exclusive; setting one clears the other
    // so XAML visibility bindings flip atomically.
    // ------------------------------------------------------------------
    private bool _isSolutionsView = true;
    public bool IsSolutionsView
    {
        get => _isSolutionsView;
        set
        {
            if (_isSolutionsView == value) return;
            _isSolutionsView = value;
            OnPropertyChanged();
            // Mutual exclusion — push the inverse onto the other property
            // without re-entering ourselves.
            if (_isFlatView == value) { _isFlatView = !value; OnPropertyChanged(nameof(IsFlatView)); }
        }
    }

    private bool _isFlatView;
    public bool IsFlatView
    {
        get => _isFlatView;
        set
        {
            if (_isFlatView == value) return;
            _isFlatView = value;
            OnPropertyChanged();
            if (_isSolutionsView == value) { _isSolutionsView = !value; OnPropertyChanged(nameof(IsSolutionsView)); }
        }
    }

    // ------------------------------------------------------------------
    // Lazy-loaded per-env Dataverse details. Fetched on first row expand
    // by InventoryViewModel.LoadEnvironmentDetailsAsync; cached in-place
    // so re-selecting the row is instant.
    // ------------------------------------------------------------------

    private IReadOnlyList<SolutionGroup> _solutions = Array.Empty<SolutionGroup>();
    public IReadOnlyList<SolutionGroup> Solutions
    {
        get => _solutions;
        set { _solutions = value; OnPropertyChanged(); OnPropertyChanged(nameof(SolutionCount)); }
    }
    public int SolutionCount => _solutions.Count;

    private IReadOnlyList<PowerPageRow> _powerPages = Array.Empty<PowerPageRow>();
    public IReadOnlyList<PowerPageRow> PowerPages
    {
        get => _powerPages;
        set { _powerPages = value; OnPropertyChanged(); OnPropertyChanged(nameof(PowerPageCount)); }
    }
    public int PowerPageCount => _powerPages.Count;

    private IReadOnlyList<UserGroupRow> _usersAndGroups = Array.Empty<UserGroupRow>();
    public IReadOnlyList<UserGroupRow> UsersAndGroups
    {
        get => _usersAndGroups;
        set { _usersAndGroups = value; OnPropertyChanged(); OnPropertyChanged(nameof(UserCount)); }
    }
    public int UserCount => _usersAndGroups.Count;

    /// <summary>True after the per-env Dataverse fetch has completed (success or failure).</summary>
    private bool _detailsLoaded;
    public bool DetailsLoaded
    {
        get => _detailsLoaded;
        set { _detailsLoaded = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// UTC timestamp of the last successful per-env Dataverse fetch (or the
    /// hydration of the cached snapshot, whichever came last). Drives the
    /// "Last refreshed: X ago" badge in the env detail header. Null until
    /// the very first expansion completes.
    /// </summary>
    private DateTime? _detailsLastSyncedUtc;
    public DateTime? DetailsLastSyncedUtc
    {
        get => _detailsLastSyncedUtc;
        set
        {
            _detailsLastSyncedUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailsLastSyncedDisplay));
            OnPropertyChanged(nameof(HasDetailsLastSynced));
        }
    }

    /// <summary>True when <see cref="DetailsLastSyncedUtc"/> is set — drives
    /// visibility of the "Last refreshed" badge in the detail header.</summary>
    public bool HasDetailsLastSynced => _detailsLastSyncedUtc.HasValue;

    /// <summary>
    /// "Cached: 5m ago" / "Cached: 2h ago" / "Cached: 2026-05-04 16:22 UTC".
    /// Computed live from <see cref="DetailsLastSyncedUtc"/>. Re-evaluated
    /// lazily by binding refreshes; we don't tick a timer.
    /// </summary>
    public string DetailsLastSyncedDisplay
    {
        get
        {
            if (!_detailsLastSyncedUtc.HasValue) return string.Empty;
            var age = DateTime.UtcNow - _detailsLastSyncedUtc.Value;
            if (age.TotalMinutes < 1)  return "Cached: just now";
            if (age.TotalMinutes < 60) return $"Cached: {(int)age.TotalMinutes}m ago";
            if (age.TotalHours   < 24) return $"Cached: {(int)age.TotalHours}h ago";
            if (age.TotalDays    < 7)  return $"Cached: {(int)age.TotalDays}d ago";
            return $"Cached: {_detailsLastSyncedUtc.Value:yyyy-MM-dd HH:mm} UTC";
        }
    }

    /// <summary>
    /// True when the user has expanded this env's row-details panel. Driven
    /// by the chevron toggle in the leading column of the env grid; the
    /// <c>DataGridRow.DetailsVisibility</c> is bound to this via a
    /// <c>DataTrigger</c> in <c>InventoryView.xaml</c>. Decoupled from
    /// <c>DataGrid.SelectedItem</c> so multiple rows can be open at once
    /// (the previous "VisibleWhenSelected" model collapsed the open row
    /// the moment another was clicked).
    /// </summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    /// <summary>True while a per-env Dataverse fetch is in flight. Bound to the row spinner.</summary>
    private bool _isLoadingDetails;
    public bool IsLoadingDetails
    {
        get => _isLoadingDetails;
        set { _isLoadingDetails = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Last error from the per-env Dataverse fetch (e.g. token failure on a
    /// non-Dataverse env, 403 because the signed-in user lacks System
    /// Administrator on the target env). Bound to a red banner inside the
    /// row-details template so the user knows why the section is empty.
    /// </summary>
    private string? _detailsError;
    public string? DetailsError
    {
        get => _detailsError;
        set { _detailsError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDetailsError)); }
    }

    public bool HasDetailsError => !string.IsNullOrEmpty(_detailsError);

    /// <summary>
    /// Deep-link to the environment "home" page in the Power Platform maker
    /// portal. Opens the env in the user's default browser when the user
    /// clicks the open-in-browser action on the env grid row. Null when the
    /// row has no env id (defensive — every PPAC row should have one).
    /// </summary>
    public string? MakerUrl
        => string.IsNullOrEmpty(EnvId)
            ? null
            : $"https://make.powerapps.com/environments/{EnvId}/home";

    /// <summary>
    /// Tri-state membership flag for the "Only my environments" toggle.
    ///   <c>null</c>   — not yet checked (skipped or pending WhoAmI call)
    ///   <c>true</c>   — signed-in user has a Dataverse <c>systemuser</c>
    ///                   record on this env (WhoAmI returned 200)
    ///   <c>false</c>  — signed-in user is NOT a member (WhoAmI 401/403,
    ///                   or env has no Dataverse to check)
    /// Drives the <see cref="VerseOps.App.Inventory.InventoryViewModel"/>
    /// collection-view filter when the toggle is on.
    /// </summary>
    private bool? _isCurrentUserMember;
    public bool? IsCurrentUserMember
    {
        get => _isCurrentUserMember;
        set { _isCurrentUserMember = value; OnPropertyChanged(); OnPropertyChanged(nameof(MembershipBadge)); }
    }

    /// <summary>
    /// Tri-state result of the Microsoft Graph <c>POST /me/checkMemberGroups</c>
    /// probe against this env's <see cref="SecurityGroupId"/>.
    ///   <c>null</c>   — env has no security group, or check hasn't run yet
    ///   <c>true</c>   — signed-in user IS a transitive member of the group
    ///   <c>false</c>  — signed-in user is NOT a member
    /// This is the gating signal for the strict "Only my environments"
    /// filter: envs with a security group AND the user inside it pass;
    /// everything else is hidden.
    /// </summary>
    private bool? _isCurrentUserInSecurityGroup;
    public bool? IsCurrentUserInSecurityGroup
    {
        get => _isCurrentUserInSecurityGroup;
        set { _isCurrentUserInSecurityGroup = value; OnPropertyChanged(); OnPropertyChanged(nameof(MembershipBadge)); }
    }

    /// <summary>One-character badge shown next to the env name when the
    /// "Only my envs" filter is on. Empty when membership is unknown.</summary>
    public string MembershipBadge => (_isCurrentUserMember == true || _isCurrentUserInSecurityGroup == true) ? "★" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
