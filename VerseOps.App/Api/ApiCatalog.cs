namespace VerseOps.App.Api;

public enum ApiSurface { Bap, Ppac, Local }

public enum ParamKind
{
    Text,             // free-form string -> ComboBox/TextBox
    Choice,           // static enum -> ComboBox
    Environment,      // dynamic -> populated from List environments
    EnvironmentGroup, // dynamic -> populated from List environment groups
    DlpPolicy,        // dynamic -> populated from List DLP policies
    BillingPolicy,    // dynamic -> populated from List billing policies
    Template,         // dynamic -> populated from List Dataverse templates (per location)
    MultilineText,
    Integer
}

public sealed record OpParam(
    string Token,                       // e.g. "environmentId" â€” replaces {environmentId}
    string Label,
    ParamKind Kind = ParamKind.Text,
    string? Default = null,
    IReadOnlyList<string>? Choices = null,
    bool Required = true,
    string? Help = null
);

/// <summary>
/// Catalog of Power Platform control-plane operations exposed by the
/// Microsoft.PowerPlatform.Management surface (BAP / PPAC).
/// Each entry is a template the user can pick, edit, and execute.
/// </summary>
public sealed record ApiOperation(
    string Category,
    string Name,
    string HttpMethod,
    string UrlTemplate,
    string TokenScope,
    string? RequestBodyTemplate,
    string Description,
    ApiSurface Surface = ApiSurface.Bap,
    IReadOnlyList<OpParam>? Parameters = null,
    string? SubCategory = null  // optional second-level grouping (e.g. "Billing Policy")
);

public static class ApiCatalog
{
    public const string ScopePowerApps = "https://service.powerapps.com/.default";
    public const string ScopePpac = "https://api.powerplatform.com/.default";
    public const string ScopeGraph = "https://graph.microsoft.com/.default";

    // ---- Shared parameter presets -------------------------------------
    public static readonly IReadOnlyList<string> Locations = new[]
    {
        "unitedstates", "europe", "asia", "australia", "india", "japan",
        "canada", "unitedkingdom", "southamerica", "france", "germany",
        "switzerland", "norway", "korea", "uae", "southafrica", "singapore"
    };
    public static readonly IReadOnlyList<string> Skus = new[]
    {
        "Sandbox", "Production", "Trial", "Developer", "Default", "Teams"
    };
    public static readonly IReadOnlyList<string> Currencies = new[]
    {
        "USD", "EUR", "GBP", "INR", "AUD", "CAD", "JPY", "CHF", "SGD", "CNY"
    };
    public static readonly IReadOnlyList<string> LanguageCodes = new[]
    {
        "1033", "1031", "1036", "1040", "1041", "1043", "1046", "1049", "2052", "3082"
    };
    public static readonly IReadOnlyList<string> SecurityGroupModes = new[] { "All", "Restricted" };

    private static OpParam EnvParam => new("environmentId", "Environment", ParamKind.Environment,
        Help: "Pick a tenant environment (loads via List environments).");
    private static OpParam TargetEnvParam => new("targetEnvironmentName", "Target environment", ParamKind.Environment,
        Help: "Destination environment for Copy.");
    private static OpParam GroupParam => new("groupId", "Environment group", ParamKind.EnvironmentGroup,
        Help: "Pick an environment group (loads via List environment groups).");
    private static OpParam DlpParam => new("policyId", "DLP policy", ParamKind.DlpPolicy,
        Help: "Pick a DLP policy (loads via List DLP policies).");
    private static OpParam BillingParam => new("policyId", "Billing policy", ParamKind.BillingPolicy,
        Help: "Pick a PPAC billing policy.");
    private static OpParam OperationIdParam => new("operationId", "Operation id", ParamKind.Text,
        Help: "From a 202 response's operation-location header.");
    private static OpParam CopyTypeParam => new("copyType", "Copy type", ParamKind.Choice,
        Default: "FullCopy", Choices: new[] { "FullCopy", "MinimalCopy" });
    private static OpParam FriendlyNameParam => new("friendlyName", "Friendly name", ParamKind.Text,
        Default: "VerseOps Copy");
    private static OpParam LabelParam => new("label", "Backup label", ParamKind.Text,
        Default: "VerseOps Backup");
    private static OpParam LocationParam => new("location", "Location", ParamKind.Choice,
        Default: "unitedstates", Choices: Locations);
    private static OpParam SkuParam => new("sku", "Org type (SKU)", ParamKind.Choice,
        Default: "Sandbox", Choices: Skus,
        Help: "SDK enum allows Developer; PPAC behaviour for Developer is undocumented â€” check the response body's environmentSku to confirm what was actually provisioned.");
    private static OpParam CurrencyParam => new("currency", "Currency", ParamKind.Choice,
        Default: "USD", Choices: Currencies);
    private static OpParam LanguageParam => new("language", "Base language LCID", ParamKind.Choice,
        Default: "1033", Choices: LanguageCodes);
    private static OpParam DisplayNameParam => new("displayName", "Display name", ParamKind.Text,
        Default: "VerseOps New");

    private const string EnvCreateBody =
@"{
  ""location"": ""{location}"",
  ""properties"": {
    ""displayName"": ""{displayName}"",
    ""environmentSku"": ""{sku}"",
    ""databaseType"": ""CommonDataService"",
    ""linkedEnvironmentMetadata"": {
      ""baseLanguage"": {language},
      ""currency"": { ""code"": ""{currency}"" },
      ""templates"": []
    }
  }
}";

    private const string TenantSettingsBody =
@"{
  ""disableNpsCommentsReachout"": null,
  ""disableNewsletterSendout"": null,
  ""disableEnvironmentCreationByNonAdminUsers"": null,
  ""disablePortalsCreationByNonAdminUsers"": null,
  ""disableSurveyFeedback"": null
}";

    public static IReadOnlyList<ApiOperation> Operations { get; } = new List<ApiOperation>
    {
        // ----- Environments -----
        new("Environments", "List environments", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments?api-version=2021-04-01&$expand=properties/billingPolicy",
            ScopePowerApps, null, "All environments visible to the caller."),

        new("Environments", "Get environment", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments/{environmentId}?api-version=2021-04-01&$expand=properties/billingPolicy",
            ScopePowerApps, null, "Single environment by id.",
            ApiSurface.Bap, new[] { EnvParam }),

        new("Environments", "Create environment", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments?api-version=2021-04-01&retainOnProvisionFailure=false",
            ScopePowerApps, EnvCreateBody, "Create environment. Verified working with admin-registered SPs (returns 202 with operation-location header).",
            ApiSurface.Bap, new[] { LocationParam, DisplayNameParam, SkuParam, CurrencyParam, LanguageParam }),

        new("Environments", "Delete environment", "DELETE",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}?api-version=2021-04-01",
            ScopePowerApps, null, "Soft-delete an environment.",
            ApiSurface.Bap, new[] { EnvParam }),

        new("Environments", "Recover environment", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}/recover?api-version=2021-04-01",
            ScopePowerApps, "{}", "Recover a soft-deleted environment within retention window.",
            ApiSurface.Bap, new[] { EnvParam }),

        new("Environments", "Reset environment", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}/reset?api-version=2021-04-01",
            ScopePowerApps,
@"{
  ""friendlyName"": ""{friendlyName}"",
  ""baseLanguageCode"": {language},
  ""currency"": { ""code"": ""{currency}"" },
  ""templates"": []
}", "Reset Dataverse in the environment.",
            ApiSurface.Bap, new[] { EnvParam, FriendlyNameParam with { Default = "VerseOps Reset" }, LanguageParam, CurrencyParam }),

        new("Environments", "Copy environment", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}/copy?api-version=2021-04-01",
            ScopePowerApps,
@"{
  ""copyType"": ""{copyType}"",
  ""targetEnvironmentName"": ""{targetEnvironmentName}"",
  ""friendlyName"": ""{friendlyName}""
}", "Copy environment to a target.",
            ApiSurface.Bap, new[] { EnvParam, TargetEnvParam, CopyTypeParam, FriendlyNameParam }),

        new("Environments", "Backup environment", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}/backups?api-version=2021-04-01",
            ScopePowerApps,
@"{
  ""label"": ""{label}""
}", "Create a manual backup.",
            ApiSurface.Bap, new[] { EnvParam, LabelParam }),

        new("Environments", "List backups", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}/backups?api-version=2021-04-01",
            ScopePowerApps, null, "List backups for an environment.",
            ApiSurface.Bap, new[] { EnvParam }),

        // ----- Lifecycle / Operations -----
        new("Operations", "Get lifecycle operation", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/lifecycleOperations/{operationId}?api-version=2021-04-01",
            ScopePowerApps, null, "Polls a long-running provisioning operation.",
            ApiSurface.Bap, new[] { OperationIdParam }),

        // ----- Tenant / Catalog data -----
        new("Tenant", "List locations (geos)", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/locations?api-version=2021-04-01",
            ScopePowerApps, null, "Power Platform geos available to the tenant."),

        new("Tenant", "List currencies (per location)", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/locations/{location}/environmentCurrencies?api-version=2021-04-01",
            ScopePowerApps, null, "Currencies supported in a geo.",
            ApiSurface.Bap, new[] { LocationParam }),

        new("Tenant", "List languages (per location)", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/locations/{location}/environmentLanguages?api-version=2021-04-01",
            ScopePowerApps, null, "Base languages supported in a geo.",
            ApiSurface.Bap, new[] { LocationParam }),

        new("Tenant", "List Dataverse templates", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/locations/{location}/environmentTemplates?api-version=2021-04-01",
            ScopePowerApps, null, "Templates installable on a new Dataverse environment.",
            ApiSurface.Bap, new[] { LocationParam }),

        new("Tenant", "Get tenant settings", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/listTenantSettings?api-version=2020-10-01",
            ScopePowerApps, "{}", "Tenant-wide Power Platform settings."),

        new("Tenant", "Update tenant settings", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/updateTenantSettings?api-version=2020-10-01",
            ScopePowerApps, TenantSettingsBody, "Patch tenant settings (only set fields you want to change)."),

        // ----- Capacity -----
        new("Capacity", "Tenant capacity", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/tenant/capacity?api-version=2021-04-01",
            ScopePowerApps, null, "Storage / file / log capacity used at tenant level."),

        new("Capacity", "Environment capacity", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}/capacity?api-version=2021-04-01",
            ScopePowerApps, null, "Storage breakdown for one environment.",
            ApiSurface.Bap, new[] { EnvParam }),

        // ----- DLP Policies -----
        new("DLP", "List DLP policies", "GET",
            "https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01",
            ScopePowerApps, null, "Tenant Data Loss Prevention policies."),

        new("DLP", "Get DLP policy", "GET",
            "https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies/{policyId}?api-version=2018-01-01",
            ScopePowerApps, null, "Single DLP policy.",
            ApiSurface.Bap, new[] { DlpParam }),

        // ----- Connectors / Connections -----
        new("Connections", "List connections (per environment)", "GET",
            "https://api.powerapps.com/providers/Microsoft.PowerApps/scopes/admin/environments/{environmentId}/connections?api-version=2020-06-01",
            ScopePowerApps, null, "Admin view of connections in an environment.",
            ApiSurface.Bap, new[] { EnvParam }),

        // ----- Apps -----
        new("Apps", "List Power Apps (per environment)", "GET",
            "https://api.powerapps.com/providers/Microsoft.PowerApps/scopes/admin/environments/{environmentId}/apps?api-version=2020-06-01",
            ScopePowerApps, null, "Canvas + model-driven apps in environment.",
            ApiSurface.Bap, new[] { EnvParam }),

        // ----- Flows -----
        new("Flows", "List flows (per environment)", "GET",
            "https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/scopes/admin/environments/{environmentId}/flows?api-version=2016-11-01",
            ScopePowerApps, null, "Power Automate flows in environment.",
            ApiSurface.Bap, new[] { EnvParam }),

        // ----- Identity / Token introspection -----
        new("Identity", "Decode current token (local)", "GET",
            "local://decode-token",
            ScopePowerApps, null, "Decodes the JWT (no network call) so you can audit oid/idtyp/scp/roles claims.",
            ApiSurface.Local),

    }.AsReadOnly();

    // -----------------------------------------------------------------
    // PPAC (api.powerplatform.com) â€” new control-plane surface
    // Mirrors the Microsoft.PowerPlatform.Management SDK shape.
    // -----------------------------------------------------------------
    public const string PpacApiVer = "api-version=2022-03-01-preview";

    /// <summary>
    /// PPAC operations sourced from the public Power Platform REST API documentation
    /// (learn.microsoft.com/rest/api/power-platform). Built by scraping every leaf page
    /// in the official TOC, so the categories/sub-categories/names exactly match what
    /// users see in the docs sidebar.
    /// </summary>
    public static IReadOnlyList<ApiOperation> PpacOperations { get; } = PpacGeneratedCatalog.Operations;


    /// <summary>Returns operations filtered by surface (BAP or PPAC).</summary>
    public static IEnumerable<ApiOperation> ForSurface(ApiSurface surface)
    {
        return surface == ApiSurface.Ppac ? PpacOperations : Operations;
    }
}
