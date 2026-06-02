namespace VerseOps.App.Configuration;

/// <summary>
/// App-wide constants that downstream code reads instead of inlining literals.
/// Anything customer-overridable lives in <see cref="AppSettings"/>; only
/// truly-fixed Microsoft-owned identifiers belong here.
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// Public well-known Azure CLI multi-tenant client id. Safe fallback that
    /// every Entra tenant trusts out of the box, so first-run customers can
    /// sign in without registering their own app first. For production /
    /// least-privilege deployments customers should register their own public
    /// client and set <c>publicClientId</c> in appsettings.json.
    /// </summary>
    public const string AzureCliPublicClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>Default tenant value for MSAL authority. "common" means any AAD tenant.</summary>
    public const string DefaultTenant = "common";
}
