using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// Inventory service contract. Loading is split from refresh so the UI can
/// snap to local SQLite immediately on startup, then trigger a background sync.
/// </summary>
public interface IInventoryService
{
    /// <summary>Read everything currently in the local SQLite catalog.</summary>
    IReadOnlyList<EnvironmentRow> Load();

    /// <summary>
    /// Tenant-wide capacity rows (Database / File / Log / FinOpsDatabase / etc.).
    /// PPAC reports storage in MB; consumers convert to GB for display.
    /// </summary>
    IReadOnlyList<TenantCapacityEntry> LoadTenantCapacity();

    /// <summary>
    /// Tenant-wide asset catalog (apps + flows + agents) from the
    /// Power Platform Inventory API. View-model groups by env_id for the
    /// per-env expander UI.
    /// </summary>
    IReadOnlyList<AssetRow> LoadAssets();

    /// <summary>Last successful sync timestamp (UTC), or null if never synced.</summary>
    DateTime? LastSyncedUtc();

    /// <summary>
    /// Pull the env list from PPAC, plus per-env capacity allocations,
    /// and persist into the local SQLite catalog. Replaces existing rows.
    /// </summary>
    Task<RefreshResult> RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="RefreshAsync"/> but invokes <paramref name="onPhaseReady"/>
    /// after each independent phase completes and lands in SQLite, so the UI
    /// can re-hydrate incrementally instead of waiting for the slowest phase.
    /// Phases run in parallel where they're independent (env list, BAP
    /// capacity, tenant capacity, Inventory API assets).
    /// </summary>
    Task<RefreshResult> RefreshAsync(
        IProgress<string>? progress,
        Func<RefreshPhase, Task>? onPhaseReady,
        CancellationToken ct);

    /// <summary>
    /// Lazy-load per-env Dataverse details (real solutions / Power Pages /
    /// users) for one environment. Results are cached on the
    /// <see cref="EnvironmentRow"/> instance so re-selecting the row is
    /// instant. Throws on auth failure (so the caller can show why); transport
    /// errors return empty sub-lists so the user can keep working on other envs.
    /// </summary>
    Task<DataverseEnvClient.EnvDetails> LoadEnvironmentDetailsAsync(
        EnvironmentRow env,
        IReadOnlyList<AssetRow> envAssets,
        CancellationToken ct = default);

    /// <summary>
    /// Cache-aware overload. When <paramref name="forceRefresh"/> is
    /// <c>false</c> the implementation MAY (and should) hydrate the result
    /// from local SQLite without touching the network. When <c>true</c>
    /// the cache is dropped first and a live Dataverse fetch is performed
    /// + persisted. Used by the per-env "Refresh" button on the row detail.
    /// </summary>
    Task<DataverseEnvClient.EnvDetails> LoadEnvironmentDetailsAsync(
        EnvironmentRow env,
        IReadOnlyList<AssetRow> envAssets,
        bool forceRefresh,
        CancellationToken ct = default);

    /// <summary>
    /// Quick membership probe (Dataverse <c>WhoAmI</c>) used by the "Only my
    /// environments" toggle. Returns <c>true</c> if the signed-in user has a
    /// systemuser record on the target env; <c>false</c> if not / no Dataverse;
    /// <c>null</c> if the check failed (transient — caller may retry).
    /// </summary>
    Task<bool?> CheckCurrentUserMembershipAsync(
        EnvironmentRow env,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the tenant-wide license catalog + assigned licenses from
    /// Microsoft Graph. Returns the populated client so callers can read
    /// <see cref="GraphLicenseClient.LicensesByUpn"/>, the SKU rollup,
    /// and any soft warnings (403, etc.).
    /// </summary>
    Task<GraphLicenseClient> LoadGraphLicensesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lazy-load the tenant-wide DLP policy list from BAP
    /// (<c>PowerPlatform.Governance/v2/policies</c>). Cached after first
    /// successful pull so re-opening the Governance drawer is instant.
    /// Throws on auth failure / non-2xx so the caller can surface the body
    /// in the error pane.
    /// </summary>
    Task<IReadOnlyList<BapDlpClient.DlpPolicyDto>> LoadDlpPoliciesAsync(CancellationToken ct = default);

    /// <summary>
    /// Single Microsoft Graph round-trip to determine which of the supplied
    /// security-group ids the signed-in user is a transitive member of.
    /// Used by the strict "Only my environments" filter — envs whose
    /// <see cref="EnvironmentRow.SecurityGroupId"/> is in the returned set
    /// pass the filter, all others (including envs with no security group)
    /// are hidden.
    /// </summary>
    Task<HashSet<string>> CheckSecurityGroupMembershipAsync(
        IEnumerable<string> groupIds,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves AAD security-group ids to their display names via Microsoft
    /// Graph. Used by the env grid's "Security Group" column so admins see
    /// "Finance Admins" instead of <c>0fa9...3d</c>. Failures return an
    /// empty dictionary (the column then falls back to the GUID prefix).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveSecurityGroupNamesAsync(
        IEnumerable<string> groupIds,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the System Administrator role from a Dataverse <c>systemuser</c>
    /// on the specified environment. Implements "Revoke Admin" from the
    /// Users sub-grid: looks up the role id, then issues
    /// <c>DELETE systemusers({id})/systemuserroles_association/$ref</c>.
    /// Throws <see cref="HttpRequestException"/> on a non-204 response so
    /// the caller can surface the body in the error pane.
    /// The signed-in user must themselves hold System Administrator on the
    /// target env, otherwise Dataverse returns 403.
    /// </summary>
    Task RevokeSystemAdminAsync(
        string instanceUrl,
        string systemUserId,
        CancellationToken ct = default);
}

public sealed record RefreshResult(int EnvironmentCount, int CapacityRows, int AssetRows, TimeSpan Duration);

/// <summary>
/// Per-phase notification for the incremental refresh path. Fired the
/// moment a phase has landed in SQLite, so the view-model can reload
/// the relevant slice without waiting for slower phases to finish.
/// </summary>
public enum RefreshPhase
{
    /// <summary>Environments + per-env capacity rows just landed.</summary>
    EnvironmentsAndCapacity,
    /// <summary>Tenant-wide capacity rollup just landed.</summary>
    TenantCapacity,
    /// <summary>Inventory API asset catalog just landed.</summary>
    Assets
}
