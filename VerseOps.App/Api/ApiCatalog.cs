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
    string Token,                       // e.g. "environmentId" — replaces {environmentId}
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
    IReadOnlyList<OpParam>? Parameters = null
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
        Help: "SDK enum allows Developer; PPAC behaviour for Developer is undocumented — check the response body's environmentSku to confirm what was actually provisioned.");
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
    // PPAC (api.powerplatform.com) — new control-plane surface
    // Mirrors the Microsoft.PowerPlatform.Management SDK shape.
    // -----------------------------------------------------------------
    private const string PpacApiVer = "api-version=2022-03-01-preview";

    public static IReadOnlyList<ApiOperation> PpacOperations { get; } = new List<ApiOperation>
    {
        // Environments — PPAC list does NOT accept $expand=properties/capacity
        // (returns 400). Use the per-env GET below if you need the capacity block.
        new("Environments", "List environments", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environments?{PpacApiVer}",
            ScopePpac, null, "PPAC: tenant environments.", ApiSurface.Ppac),

        new("Environments", "Get environment", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: single environment.", ApiSurface.Ppac, new[] { EnvParam }),

        new("Environments", "Create environment", "POST",
            $"https://api.powerplatform.com/environmentmanagement/environments?{PpacApiVer}",
            ScopePpac, EnvCreateBody, "PPAC: provision a new environment.", ApiSurface.Ppac,
            new[] { LocationParam, DisplayNameParam, SkuParam, CurrencyParam, LanguageParam }),

        new("Environments", "Delete environment", "DELETE",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: soft-delete environment.", ApiSurface.Ppac, new[] { EnvParam }),

        // Environment Groups (PPAC-only)
        new("EnvironmentGroups", "List environment groups", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environmentGroups?{PpacApiVer}",
            ScopePpac, null, "PPAC: environment groups (no BAP equivalent).", ApiSurface.Ppac),

        new("EnvironmentGroups", "Get environment group", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environmentGroups/{{groupId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: single environment group.", ApiSurface.Ppac, new[] { GroupParam }),

        // Operations / lifecycle
        new("Operations", "Get operation", "GET",
            $"https://api.powerplatform.com/environmentmanagement/operations/{{operationId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: long-running operation status.", ApiSurface.Ppac, new[] { OperationIdParam }),

        // Tenant — NOTE: there is no verified PPAC route for tenant info / settings.
        //   /tenant and /tenant/settings on api.powerplatform.com return RouteNotFound.
        //   Use BAP's "Get tenant settings" entry instead.

        // Licensing / billing
        new("Billing", "List billing policies", "GET",
            $"https://api.powerplatform.com/licensing/billingPolicies?{PpacApiVer}",
            ScopePpac, null, "PPAC: pay-as-you-go billing policies (no BAP equivalent).", ApiSurface.Ppac),

        new("Billing", "Get billing policy", "GET",
            $"https://api.powerplatform.com/licensing/billingPolicies/{{policyId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: single billing policy.", ApiSurface.Ppac, new[] { BillingParam }),

        // Capacity
        new("Capacity", "Tenant capacity", "GET",
            $"https://api.powerplatform.com/licensing/tenantCapacity?{PpacApiVer}",
            ScopePpac, null, "PPAC: tenant capacity rollup.", ApiSurface.Ppac),

        new("Capacity", "Environment capacity ($expand)", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}?{PpacApiVer}&$expand=properties/capacity",
            ScopePpac, null, "PPAC: per-env capacity via env Get with $expand=properties/capacity. The capacity block is nested inside properties.capacity.",
            ApiSurface.Ppac, new[] { EnvParam }),

        new("Licensing", "Tenant licenses", "GET",
            $"https://api.powerplatform.com/licensing/tenantLicenses?{PpacApiVer}",
            ScopePpac, null, "PPAC: tenant licenses. Requires PAYG/billing-enabled tenant — returns RouteNotFound otherwise.", ApiSurface.Ppac),

        new("Licensing", "Product inventory", "GET",
            $"https://api.powerplatform.com/licensing/productInventory?{PpacApiVer}",
            ScopePpac, null, "PPAC: product inventory across the tenant. Requires PAYG/billing-enabled tenant.", ApiSurface.Ppac),

        new("Licensing", "Currency reports", "GET",
            $"https://api.powerplatform.com/licensing/currencyReports?{PpacApiVer}",
            ScopePpac, null, "PPAC: currency / consumption reports. Requires PAYG/billing-enabled tenant.", ApiSurface.Ppac),

        // Governance / DLP
        // NOTE: rule-based policies route requires the api-version query param;
        //       the v1 prefix variant returns RouteNotFound. DLP itself has no PPAC
        //       equivalent yet — use the BAP entry under the BAP surface.
        new("Governance", "List rule-based policies", "GET",
            $"https://api.powerplatform.com/governance/ruleBasedPolicies?{PpacApiVer}",
            ScopePpac, null, "PPAC: rule-based governance policies.", ApiSurface.Ppac),

        // App management — PPAC exposes installed *application packages* per env,
        // not the BAP-style /apps list. Use the BAP surface for canvas/MD apps.
        new("Apps", "List application packages", "GET",
            $"https://api.powerplatform.com/appmanagement/environments/{{environmentId}}/applicationPackages?{PpacApiVer}",
            ScopePpac, null, "PPAC: installed application packages for an environment.", ApiSurface.Ppac, new[] { EnvParam }),

        // Power Pages — the resource is /websites on PPAC (not /sites).
        new("PowerPages", "List websites (per environment)", "GET",
            $"https://api.powerplatform.com/powerpages/environments/{{environmentId}}/websites?{PpacApiVer}",
            ScopePpac, null, "PPAC: Power Pages websites in an environment.", ApiSurface.Ppac, new[] { EnvParam }),

        // Users — /usermanagement is in the SDK shape but the public PPAC surface
        // does not expose it (RouteNotFound). For per-env users use the Dataverse
        // systemusers entity in that environment, or the BAP delegated user APIs.

        // Connectors
        // PPAC requires an explicit $filter on environment id; without it the route
        // returns 400 MissingEnvironmentFilter. The {environmentId} token is reused
        // inside the filter for convenience.
        new("Connectors", "List connectors (per environment)", "GET",
            $"https://api.powerplatform.com/connectivity/connectors?{PpacApiVer}&$filter=environment eq '{{environmentId}}'",
            ScopePpac, null, "PPAC: connectors in environment.", ApiSurface.Ppac, new[] { EnvParam }),

        new("Connections", "List connections (per environment)", "GET",
            $"https://api.powerplatform.com/connectivity/environments/{{environmentId}}/connections?{PpacApiVer}",
            ScopePpac, null, "PPAC: connections in environment.", ApiSurface.Ppac, new[] { EnvParam }),

        // Environment lifecycle (PPAC equivalents to BAP recover/reset/copy/backup).
        // Routes are documented in the Microsoft.PowerPlatform.Management SDK but
        // most still return RouteNotFound on api.powerplatform.com today (preview).
        // Listed here so users can re-test once Microsoft GAs the surface.
        new("Environments", "Recover environment", "POST",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}/recover?{PpacApiVer}",
            ScopePpac, "{}", "PPAC (preview): recover soft-deleted environment.",
            ApiSurface.Ppac, new[] { EnvParam }),

        new("Environments", "Reset environment", "POST",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}/reset?{PpacApiVer}",
            ScopePpac,
@"{
  ""friendlyName"": ""{friendlyName}"",
  ""baseLanguageCode"": {language},
  ""currency"": { ""code"": ""{currency}"" },
  ""templates"": []
}", "PPAC (preview): reset Dataverse in environment.",
            ApiSurface.Ppac, new[] { EnvParam, FriendlyNameParam with { Default = "VerseOps Reset" }, LanguageParam, CurrencyParam }),

        new("Environments", "Copy environment", "POST",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}/copy?{PpacApiVer}",
            ScopePpac,
@"{
  ""copyType"": ""{copyType}"",
  ""targetEnvironmentName"": ""{targetEnvironmentName}"",
  ""friendlyName"": ""{friendlyName}""
}", "PPAC (preview): copy environment to target.",
            ApiSurface.Ppac, new[] { EnvParam, TargetEnvParam, CopyTypeParam, FriendlyNameParam }),

        new("Environments", "Backup environment", "POST",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}/backups?{PpacApiVer}",
            ScopePpac, @"{ ""label"": ""{label}"" }", "PPAC (preview): create manual backup.",
            ApiSurface.Ppac, new[] { EnvParam, LabelParam }),

        new("Environments", "List backups", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}/backups?{PpacApiVer}",
            ScopePpac, null, "PPAC (preview): list backups.",
            ApiSurface.Ppac, new[] { EnvParam }),

        new("Environments", "Get managed-env settings", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environments/{{environmentId}}/managedEnvironment?{PpacApiVer}",
            ScopePpac, null, "PPAC: managed environment settings (verified working for managed envs).",
            ApiSurface.Ppac, new[] { EnvParam }),

        // Analytics (PPAC) — surfaced via the SDK's Analytics namespace.
        // AdvisorRecommendations was verified working during the reflection sweep.
        new("Analytics", "Advisor recommendations", "GET",
            $"https://api.powerplatform.com/analytics/advisorRecommendations?{PpacApiVer}",
            ScopePpac, null, "PPAC: tenant advisor recommendations (verified).", ApiSurface.Ppac),

        new("Analytics", "Environment-scoped advisor recommendations", "GET",
            $"https://api.powerplatform.com/analytics/environments/{{environmentId}}/advisorRecommendations?{PpacApiVer}",
            ScopePpac, null, "PPAC (preview): per-env advisor recommendations.",
            ApiSurface.Ppac, new[] { EnvParam }),

        // Power Apps (PPAC) — empty list during sweep but route shape from SDK.
        new("Apps", "List Power Apps (PPAC)", "GET",
            $"https://api.powerplatform.com/powerapps/environments/{{environmentId}}/apps?{PpacApiVer}",
            ScopePpac, null, "PPAC (preview): canvas/MD apps in env. Empty for SP today; falls back to BAP for real data.",
            ApiSurface.Ppac, new[] { EnvParam }),

        // User management (PPAC) — listed as RouteNotFound today.
        new("Users", "List users (PPAC)", "GET",
            $"https://api.powerplatform.com/usermanagement/users?{PpacApiVer}",
            ScopePpac, null, "PPAC (not yet exposed): tenant Power Platform users. Currently returns RouteNotFound — listed for future GA.",
            ApiSurface.Ppac),

        new("Users", "List environment users", "GET",
            $"https://api.powerplatform.com/usermanagement/environments/{{environmentId}}/users?{PpacApiVer}",
            ScopePpac, null, "PPAC (not yet exposed): users in environment. Currently RouteNotFound.",
            ApiSurface.Ppac, new[] { EnvParam }),

        // Power Virtual Agents / Copilot Studio (PPAC).
        new("CopilotStudio", "List bots (per environment)", "GET",
            $"https://api.powerplatform.com/powervirtualagents/environments/{{environmentId}}/bots?{PpacApiVer}",
            ScopePpac, null, "PPAC (preview): Copilot Studio / PVA bots in environment.",
            ApiSurface.Ppac, new[] { EnvParam }),

        // Identity (shared)
        new("Identity", "Decode current token (local)", "GET",
            "local://decode-token",
            ScopePpac, null, "Decodes JWT for the PPAC scope so you can verify audience.",
            ApiSurface.Local),

        // ================================================================
        // Full SDK surface — auto-imported from Microsoft.PowerPlatform.Management
        // reflection sweep. Many of these return RouteNotFound today; they are
        // listed so the tree shows every route the SDK exposes. Once Microsoft
        // GAs api.powerplatform.com these should light up — at which point we
        // will move successful ones into the curated section above.
        // ================================================================

        // Analytics
        new("Analytics (full SDK)", "Advisor recommendation by id", "GET",
            $"https://api.powerplatform.com/analytics/advisorRecommendations/{{recommendationId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: single advisor recommendation by id.",
            ApiSurface.Ppac, new[] { new OpParam("recommendationId", "Recommendation id", ParamKind.Text) }),
        new("Analytics (full SDK)", "Advisor scenarios", "GET",
            $"https://api.powerplatform.com/analytics/advisorRecommendations/scenarios?{PpacApiVer}",
            ScopePpac, null, "PPAC: list advisor scenarios.", ApiSurface.Ppac),

        // App management — tenant-wide application packages
        new("Apps (full SDK)", "Application packages (tenant)", "GET",
            $"https://api.powerplatform.com/appmanagement/applicationPackages?{PpacApiVer}",
            ScopePpac, null, "PPAC: tenant-wide application packages catalog.", ApiSurface.Ppac),

        // Environment management — env group operations
        new("EnvironmentGroups (full SDK)", "Environment group operation by id", "GET",
            $"https://api.powerplatform.com/environmentmanagement/environmentGroupOperations/{{operationId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: status of an env-group long-running operation.",
            ApiSurface.Ppac, new[] { OperationIdParam }),

        // Governance
        new("Governance (full SDK)", "Cross-tenant connection reports", "GET",
            $"https://api.powerplatform.com/governance/crossTenantConnectionReports?{PpacApiVer}",
            ScopePpac, null, "PPAC: cross-tenant connection reports across the tenant.", ApiSurface.Ppac),
        new("Governance (full SDK)", "Cross-tenant connection report by env", "GET",
            $"https://api.powerplatform.com/governance/crossTenantConnectionReports/{{environmentId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: cross-tenant connection report for one env.",
            ApiSurface.Ppac, new[] { EnvParam }),
        new("Governance (full SDK)", "Rule-based policy assignments", "GET",
            $"https://api.powerplatform.com/governance/ruleBasedPolicies/assignments?{PpacApiVer}",
            ScopePpac, null, "PPAC: assignments across all rule-based policies.", ApiSurface.Ppac),
        new("Governance (full SDK)", "Rule-based policy by id", "GET",
            $"https://api.powerplatform.com/governance/ruleBasedPolicies/{{policyId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: single rule-based policy by id.",
            ApiSurface.Ppac, new[] { new OpParam("policyId", "Policy id", ParamKind.Text) }),
        new("Governance (full SDK)", "Shared connectors", "GET",
            $"https://api.powerplatform.com/governance/sharedConnectors?{PpacApiVer}",
            ScopePpac, null, "PPAC: shared connectors across the tenant.", ApiSurface.Ppac),

        // Licensing
        new("Licensing (full SDK)", "Licensing per environment", "GET",
            $"https://api.powerplatform.com/licensing/environments/{{environmentId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: licensing details for one environment.",
            ApiSurface.Ppac, new[] { EnvParam }),
        new("Licensing (full SDK)", "ISV contracts", "GET",
            $"https://api.powerplatform.com/licensing/isvContracts?{PpacApiVer}",
            ScopePpac, null, "PPAC: ISV contract list.", ApiSurface.Ppac),
        new("Licensing (full SDK)", "ISV contract by id", "GET",
            $"https://api.powerplatform.com/licensing/isvContracts/{{contractId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: single ISV contract.",
            ApiSurface.Ppac, new[] { new OpParam("contractId", "Contract id", ParamKind.Text) }),
        new("Licensing (full SDK)", "All storage warnings", "GET",
            $"https://api.powerplatform.com/licensing/storageWarning/getAllStorageWarnings?{PpacApiVer}",
            ScopePpac, null, "PPAC: every storage warning across the tenant.", ApiSurface.Ppac),
        new("Licensing (full SDK)", "Storage warning by environment", "GET",
            $"https://api.powerplatform.com/licensing/storageWarning/{{environmentId}}?{PpacApiVer}",
            ScopePpac, null, "PPAC: storage warning details for one environment.",
            ApiSurface.Ppac, new[] { EnvParam }),

    }.AsReadOnly();

    /// <summary>Returns operations filtered by surface (BAP or PPAC).</summary>
    public static IEnumerable<ApiOperation> ForSurface(ApiSurface surface)
    {
        return surface == ApiSurface.Ppac ? PpacOperations : Operations;
    }
}
