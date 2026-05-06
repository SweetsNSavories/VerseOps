namespace VerseOps.App.Inventory.Models;

/// <summary>
/// One Power Platform asset (app / flow / agent) returned by the
/// Power Platform Inventory API:
/// <code>
///   POST https://api.powerplatform.com/resourcequery/resources/query?api-version=2024-10-01
/// </code>
/// Single tenant-wide call returns every canvas app, model-driven app, code app,
/// cloud flow, agent flow, and Copilot Studio agent across every environment.
/// We persist these into <c>gov_asset</c> and join them per-env in the UI.
///
/// Implements <see cref="System.ComponentModel.INotifyPropertyChanged"/> because
/// <see cref="OwnerName"/> and <see cref="SolutionName"/> are enriched after the
/// row is already bound to a grid (Graph user lookup + Dataverse solution
/// bucketing complete on background threads).
/// </summary>
public sealed class AssetRow : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Resource Guid (the <c>name</c> field on the ARM record).</summary>
    public required string AssetId { get; init; }

    /// <summary>
    /// Resource sub-type after stripping the vendor prefix, e.g.
    /// <c>canvasapps</c>, <c>modeldrivenapps</c>, <c>codeapps</c>,
    /// <c>cloudflows</c>, <c>agentflows</c>, <c>agents</c>.
    /// Stored without the <c>microsoft.&lt;vendor&gt;/</c> prefix to keep
    /// SQL filters tidy and survive future provider renames.
    /// </summary>
    public required string AssetType { get; init; }

    /// <summary>
    /// Owning environment Id (<c>properties.environmentId</c>). Lower-cased so
    /// joins against <see cref="EnvironmentRow.EnvId"/> are case-insensitive.
    /// May be null for tenant-scoped resources (e.g. environment groups).
    /// </summary>
    public string? EnvId { get; set; }

    public string? DisplayName { get; set; }
    public string? OwnerId { get; set; }
    public string? CreatedBy { get; set; }
    public string? Region { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public bool? IsQuarantined { get; set; }
    public DateTime LastSyncedUtc { get; set; }

    private string? _ownerName;
    /// <summary>
    /// Friendly owner display label (e.g. "Jane Doe (jane@contoso.com)") resolved
    /// from <see cref="OwnerId"/> via Microsoft Graph after the row is bound.
    /// Falls back to <see cref="OwnerId"/> in the UI when null (until enrichment
    /// completes or for owners that aren't found in the directory — service
    /// principals, deleted users, etc.).
    /// </summary>
    public string? OwnerName
    {
        get => _ownerName;
        set { if (_ownerName == value) return; _ownerName = value; OnPropertyChanged(); OnPropertyChanged(nameof(OwnerDisplay)); }
    }

    /// <summary>UI binding helper: prefer the friendly name, fall back to the raw GUID.</summary>
    public string OwnerDisplay => _ownerName ?? OwnerId ?? string.Empty;

    private string? _solutionName;
    /// <summary>
    /// Owning Dataverse solution friendly name. Populated by
    /// <see cref="VerseOps.App.Inventory.Services.DataverseEnvClient"/> while
    /// it's bucketing assets into <see cref="SolutionGroup.Apps"/> /
    /// <see cref="SolutionGroup.Flows"/> / <see cref="SolutionGroup.Agents"/>.
    /// "(unmatched)" for assets the catalog couldn't trace to a visible
    /// solution; null until the per-env detail load runs.
    /// </summary>
    public string? SolutionName
    {
        get => _solutionName;
        set { if (_solutionName == value) return; _solutionName = value; OnPropertyChanged(); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>
    /// Human-friendly label derived from <see cref="AssetType"/> for UI badges
    /// (e.g. "Canvas App", "Cloud Flow", "Agent").
    /// </summary>
    public string TypeDisplay => AssetType switch
    {
        "canvasapps"       => "Canvas App",
        "modeldrivenapps"  => "Model-driven App",
        "codeapps"         => "Code App",
        "apps"             => "App Builder",
        "cloudflows"       => "Cloud Flow",
        "agentflows"       => "Agent Flow",
        "m365agentflows"   => "Workflow Agent",
        "agents"           => "Copilot Agent",
        _                  => AssetType
    };

    /// <summary>
    /// Deep-link to the asset in the appropriate maker portal.
    /// Returns null if we can't compose a URL (missing EnvId / unknown type).
    ///
    /// IMPORTANT — model-driven / code apps must use the explicit
    /// <c>/details</c> sub-route. The bare <c>/apps/{id}</c> URL is parsed by
    /// the maker portal as an action route and on some tenants will silently
    /// fire the row's default action (e.g. "Successfully deactivated
    /// model-driven app.") instead of opening a details panel.
    ///
    /// Routes per type:
    ///   canvasapps      → make.powerapps.com/environments/{env}/apps/canvas/{id}
    ///   modeldrivenapps → make.powerapps.com/environments/{env}/apps/{id}/details
    ///   codeapps/apps   → make.powerapps.com/environments/{env}/apps/{id}/details
    ///   cloudflows      → make.powerautomate.com/environments/{env}/flows/{id}/details
    ///   agentflows      → make.powerautomate.com/environments/{env}/agentflows/{id}/details
    ///   agents          → copilotstudio.microsoft.com/environments/{env}/bots/{id}
    /// </summary>
    public string? MakerUrl
    {
        get
        {
            if (string.IsNullOrEmpty(EnvId) || string.IsNullOrEmpty(AssetId)) return null;
            return AssetType switch
            {
                "canvasapps"       => $"https://make.powerapps.com/environments/{EnvId}/apps/canvas/{AssetId}",
                "modeldrivenapps"  => $"https://make.powerapps.com/environments/{EnvId}/apps/{AssetId}/details",
                "codeapps"         => $"https://make.powerapps.com/environments/{EnvId}/apps/{AssetId}/details",
                "apps"             => $"https://make.powerapps.com/environments/{EnvId}/apps/{AssetId}/details",
                "cloudflows"       => $"https://make.powerautomate.com/environments/{EnvId}/flows/{AssetId}/details",
                "agentflows"       => $"https://make.powerautomate.com/environments/{EnvId}/agentflows/{AssetId}/details",
                "m365agentflows"   => $"https://make.powerautomate.com/environments/{EnvId}/flows/{AssetId}/details",
                "agents"           => $"https://copilotstudio.microsoft.com/environments/{EnvId}/bots/{AssetId}",
                _                  => null
            };
        }
    }
}
