namespace VerseOps.Configuration;

/// <summary>
/// Options required to authenticate app-only against the Power Platform control plane.
///
/// Why app-only works for environment creation:
///   The BAP / PPAC control-plane APIs (api.bap.microsoft.com) accept tokens issued for
///   the "PowerApps Service" first-party resource (audience https://service.powerapps.com/).
///   When the calling Azure AD application has been granted the Power Platform Administrator
///   directory role (assignable to service principals), it can perform tenant-wide environment
///   management WITHOUT a signed-in user, a Dataverse application user, or a Power Apps license.
///   This is exactly why we can avoid Dataverse.Client / Web API and any delegated/OBO flow.
/// </summary>
public sealed class PowerPlatformOptions
{
    /// <summary>Azure AD tenant id (GUID) hosting both the app registration and the Power Platform tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Application (client) id of the Azure AD app registration.</summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// Client secret for the app registration. For production, prefer a certificate
    /// (extend this options class with a CertificateThumbprint / X509Certificate2 instead).
    /// </summary>
    public required string ClientSecret { get; set; }

    /// <summary>BAP control-plane base URL. Override only for sovereign clouds.</summary>
    public string BapBaseUrl { get; set; } = "https://api.bap.microsoft.com";

    /// <summary>Resource / scope for token acquisition. Override only for sovereign clouds.</summary>
    public string PowerPlatformScope { get; set; } = "https://service.powerapps.com/.default";

    /// <summary>BAP API version used for environment management calls.</summary>
    public string ApiVersion { get; set; } = "2021-04-01";

    /// <summary>How often to poll a long-running provisioning operation.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum total time to wait for an environment to finish provisioning.</summary>
    public TimeSpan ProvisioningTimeout { get; set; } = TimeSpan.FromMinutes(60);
}
