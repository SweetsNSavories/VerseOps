namespace VerseOps.App.Inventory.Models;

/// <summary>
/// One Dataverse solution inside an environment, with the Inventory-API
/// assets (apps / flows / agents) bucketed by their owning solution.
/// Populated by <see cref="VerseOps.App.Inventory.Services.DataverseEnvClient"/>
/// when the user expands an environment row.
/// </summary>
public sealed class SolutionGroup
{
    /// <summary>Friendly display name (e.g. "Customer Service Hub").</summary>
    public required string Name { get; init; }

    /// <summary>Dataverse solution unique name (e.g. "msdyn_CustomerService").</summary>
    public string? UniqueName { get; set; }

    /// <summary>True for solutions installed by Microsoft / ISVs (managed bit).</summary>
    public bool IsManaged { get; set; }

    /// <summary>"Friendly Name (uniquename)" of the publisher.</summary>
    public string? Publisher { get; set; }

    /// <summary>Solution version string (e.g. "9.2.26035.200").</summary>
    public string? Version { get; set; }

    /// <summary>Dataverse solutionid GUID.</summary>
    public string? SolutionId { get; set; }

    /// <summary>Owning env id — needed to compose the maker portal URL.</summary>
    public string? EnvId { get; set; }

    /// <summary>"Managed" / "Unmanaged" — derived from <see cref="IsManaged"/> for the State column.</summary>
    public string State => IsManaged ? "Managed" : "Unmanaged";

    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }

    /// <summary>Raw Dataverse JSON for the metadata inspector dialog.</summary>
    public string? RawJson { get; set; }

    /// <summary>Deep-link to the solution in the maker portal.</summary>
    public string? MakerUrl
        => string.IsNullOrEmpty(EnvId) || string.IsNullOrEmpty(SolutionId)
            ? null
            : $"https://make.powerapps.com/environments/{EnvId}/solutions/{SolutionId}";

    public IReadOnlyList<AssetRow> Apps { get; set; } = Array.Empty<AssetRow>();
    public IReadOnlyList<AssetRow> Flows { get; set; } = Array.Empty<AssetRow>();
    public IReadOnlyList<AssetRow> Agents { get; set; } = Array.Empty<AssetRow>();

    public int AppCount   => Apps.Count;
    public int FlowCount  => Flows.Count;
    public int AgentCount => Agents.Count;
    public int TotalCount => Apps.Count + Flows.Count + Agents.Count;

    /// <summary>One-line summary used by the row-details Expander header badge.</summary>
    public string Summary
        => $"{TotalCount} components   • {AppCount} apps   • {FlowCount} flows   • {AgentCount} agents";
}

/// <summary>
/// One Power Pages site (Dataverse <c>mspp_website</c> table). Populated by
/// <see cref="VerseOps.App.Inventory.Services.DataverseEnvClient"/> on row
/// expand. Falls back to the legacy portals table (<c>adx_website</c>) for
/// older envs that haven't migrated to Power Pages yet.
/// </summary>
public sealed class PowerPageRow
{
    public required string Name { get; init; }
    public string? WebsiteId { get; set; }
    public string? PrimaryDomain { get; set; }
    public string? Status { get; set; }
    public string? WebsiteType { get; set; }
    public string? LanguageCode { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }

    /// <summary>Owning env id — needed to compose the maker portal URL.</summary>
    public string? EnvId { get; set; }

    /// <summary>Raw Dataverse JSON for the metadata inspector dialog.</summary>
    public string? RawJson { get; set; }

    /// <summary>HTTPS URL of the live site (same as <see cref="PrimaryDomain"/> with scheme).</summary>
    public string? SiteUrl
        => string.IsNullOrEmpty(PrimaryDomain) ? null
            : (PrimaryDomain!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? PrimaryDomain
                : $"https://{PrimaryDomain}");

    /// <summary>Deep-link to the site in the Power Pages design studio.</summary>
    public string? MakerUrl
        => string.IsNullOrEmpty(EnvId) || string.IsNullOrEmpty(WebsiteId)
            ? null
            : $"https://make.powerpages.microsoft.com/e/{EnvId}/sites/{WebsiteId}/pages";
}

/// <summary>
/// One Dataverse <c>systemuser</c> row, plus a few derived attributes for
/// the row-details "Users &amp; Groups" grid. App-User / Application-User
/// rows are surfaced because they're the most common DevOps audit target
/// (service principals, integration users, etc.).
///
/// Implements INPC because the Revoke Admin action mutates
/// <see cref="AdminStatus"/> after the row is already in the bound grid,
/// and the <see cref="License"/> field is enriched from Microsoft Graph
/// after the initial Dataverse pull completes.
/// </summary>
public sealed class UserGroupRow : System.ComponentModel.INotifyPropertyChanged
{
    public required string DisplayName { get; init; }

    /// <summary>UPN / domain name (e.g. <c>user@contoso.com</c>).</summary>
    public string? Identity { get; set; }

    /// <summary>"App User" / "Standard User" / "Stub User" / "Group" — derived from accessmode.</summary>
    public string? SecurityAccess { get; set; }

    private string? _adminStatus;
    /// <summary>"Admin" / "Non-Admin" — derived from System Administrator role membership. Mutated by Revoke Admin.</summary>
    public string? AdminStatus
    {
        get => _adminStatus;
        set { if (_adminStatus == value) return; _adminStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAdmin)); }
    }

    /// <summary>True when the user currently has the System Administrator role — drives the Revoke Admin button visibility.</summary>
    public bool IsAdmin => string.Equals(AdminStatus, "Admin", System.StringComparison.OrdinalIgnoreCase);

    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }

    /// <summary>Dataverse <c>systemuserid</c>.</summary>
    public string? SystemUserId { get; set; }

    /// <summary>Owning env Dataverse instance URL — needed to deep-link to the user record.</summary>
    public string? InstanceUrl { get; set; }

    /// <summary>Owning env id (used by status messages / logging only).</summary>
    public string? EnvId { get; set; }

    /// <summary>Raw Dataverse JSON for the metadata inspector dialog.</summary>
    public string? RawJson { get; set; }

    private string? _license;
    /// <summary>Compact license summary for the grid cell (e.g. "POWER_BI_PRO + 2 more"). Populated by GraphLicenseClient.</summary>
    public string? License
    {
        get => _license;
        set { if (_license == value) return; _license = value; OnPropertyChanged(); }
    }

    private string? _licenseDetails;
    /// <summary>Full newline-separated license list for the Identity column tooltip. Populated by GraphLicenseClient.</summary>
    public string? LicenseDetails
    {
        get => _licenseDetails;
        set { if (_licenseDetails == value) return; _licenseDetails = value; OnPropertyChanged(); }
    }

    /// <summary>Opens the user record in classic Dataverse model form.</summary>
    public string? MakerUrl
        => string.IsNullOrEmpty(InstanceUrl) || string.IsNullOrEmpty(SystemUserId)
            ? null
            : $"{InstanceUrl!.TrimEnd('/')}/main.aspx?etn=systemuser&id={SystemUserId}&pagetype=entityrecord";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

