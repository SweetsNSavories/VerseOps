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

    private bool? _isManaged;
    /// <summary>
    /// True if the asset belongs to a Managed solution, false if Unmanaged,
    /// null if we couldn't trace the asset to any visible solution (the
    /// "(unmatched)" / Default Solution bucket). Stamped by
    /// <see cref="VerseOps.App.Inventory.Services.DataverseEnvClient"/>
    /// alongside <see cref="SolutionName"/>; carries no extra HTTP cost
    /// because the <c>solutions.ismanaged</c> flag is already in the row we
    /// already pulled for the Solutions grid.
    /// </summary>
    public bool? IsManaged
    {
        get => _isManaged;
        set { if (_isManaged == value) return; _isManaged = value; OnPropertyChanged(); OnPropertyChanged(nameof(ManagedDisplay)); }
    }

    /// <summary>UI binding helper: "Managed" / "Unmanaged" / "—".</summary>
    public string ManagedDisplay => _isManaged switch
    {
        true  => "Managed",
        false => "Unmanaged",
        _     => "—"
    };

    private string? _status;
    /// <summary>
    /// Lifecycle state of the asset, normalised across the three Dataverse
    /// tables that own each kind:
    ///   workflows.statecode (cloud flows)            → "On" | "Off" | "Suspended"
    ///   canvasapps.statecode (canvas / code apps)    → "Ready" | "Inactive"
    ///   appmodule.statecode (model-driven apps)      → "Ready" | "Inactive"
    /// Null until the asset-status loader completes (or for assets whose
    /// table doesn't carry a state field — e.g. agents pre-2025).
    /// </summary>
    public string? Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }

    private string? _uiKind;
    /// <summary>
    /// Form factor for canvas + code apps (Tablet / Phone / Web). Null on
    /// model-driven apps (always render in the model-driven shell), flows,
    /// and agents. Stamped by the per-env BAP enrichment that runs only
    /// when the user expands the env row.
    /// </summary>
    public string? UiKind
    {
        get => _uiKind;
        set { if (_uiKind == value) return; _uiKind = value; OnPropertyChanged(); }
    }

    private bool? _isPremium;
    /// <summary>
    /// True when the asset uses at least one premium connector (anything
    /// outside the curated Microsoft "standard" list — Office 365, Teams,
    /// SharePoint, OneDrive, Forms, Planner, Outlook.com, Excel, OneNote,
    /// To Do, Stream, Approvals, Power BI standard ops, etc.). Stamped from
    /// canvas-app <c>connectionreferences</c> during the per-env load.
    /// Null until the canvas enrichment runs OR for asset kinds we can't
    /// inspect (cloud flows / agents — TBD).
    /// </summary>
    public bool? IsPremium
    {
        get => _isPremium;
        set { if (_isPremium == value) return; _isPremium = value; OnPropertyChanged(); OnPropertyChanged(nameof(PremiumDisplay)); }
    }

    /// <summary>UI binding helper: "Premium" / "Standard" / "—".</summary>
    public string PremiumDisplay => _isPremium switch
    {
        true  => "Premium",
        false => "Standard",
        _     => "—"
    };

    private string? _dlpStatus;
    /// <summary>
    /// DLP compliance for this asset against the cached tenant DLP policy
    /// list, evaluated at env-expand time:
    ///   "Compliant" — no in-scope policy is violated by this asset's connectors
    ///   "Violation" — at least one connector is Blocked, or the connectors
    ///                 are split across the Business / Non-Business buckets
    ///                 of an in-scope policy
    ///   "—"         — not yet evaluated, or not evaluable (connectors
    ///                 unknown — e.g. cloud-flow or agent inspection still
    ///                 deferred)
    /// </summary>
    public string? DlpStatus
    {
        get => _dlpStatus;
        set { if (_dlpStatus == value) return; _dlpStatus = value; OnPropertyChanged(); }
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
