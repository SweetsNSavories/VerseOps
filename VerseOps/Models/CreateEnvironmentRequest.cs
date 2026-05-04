namespace VerseOps.Models;

/// <summary>
/// Input to <see cref="Services.IEnvironmentProvisioningService.CreateEnvironmentAsync"/>.
/// </summary>
public sealed record CreateEnvironmentRequest
{
    /// <summary>Friendly display name shown in Power Platform Admin Center.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Sandbox or Production SKU.</summary>
    public required EnvironmentType EnvironmentType { get; init; }

    /// <summary>
    /// Azure region / Power Platform location code (e.g. "unitedstates", "europe", "australia").
    /// Must be a region your tenant is licensed for.
    /// </summary>
    public required string Region { get; init; }

    /// <summary>If true, a Dataverse database will be provisioned alongside the environment.</summary>
    public required bool ProvisionDataverse { get; init; }

    /// <summary>Currency code (only used when Dataverse is provisioned). Defaults to USD.</summary>
    public string CurrencyCode { get; init; } = "USD";

    /// <summary>Language LCID (only used when Dataverse is provisioned). Defaults to 1033 (en-US).</summary>
    public int LanguageCode { get; init; } = 1033;

    /// <summary>Optional domain name for the Dataverse organization (org{n}.crm.dynamics.com prefix).</summary>
    public string? DomainName { get; init; }

    /// <summary>Optional Azure AD security group object id used to restrict environment access.</summary>
    public string? SecurityGroupId { get; init; }

    /// <summary>
    /// Optional Entra (Azure AD) user object id who will own the environment.
    /// REQUIRED for Developer environments — Microsoft does not allow a service
    /// principal to own a Developer SKU. The user must hold the Power Apps
    /// Developer Plan (or equivalent) license. Sent to BAP as
    /// "properties.principalUserId".
    /// </summary>
    public string? PrincipalOwnerId { get; init; }
}
