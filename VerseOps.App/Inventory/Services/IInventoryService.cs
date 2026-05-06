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
}

public sealed record RefreshResult(int EnvironmentCount, int CapacityRows, int AssetRows, TimeSpan Duration);
