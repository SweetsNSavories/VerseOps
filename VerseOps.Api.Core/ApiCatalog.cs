namespace VerseOps.Api.Core;

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

    // PPAC /environmentmanagement/provisioning/create body (CreateEnvironmentRequest).
    // Flatter shape than the BAP EnvCreateBody — no outer "properties" wrapper.
    private const string ProvisionCreateBody =
@"{
  ""displayName"": ""{displayName}"",
  ""environmentSku"": ""{sku}"",
  ""location"": ""{location}"",
  ""databaseType"": ""CommonDataService"",
  ""description"": ""Created by VerseOps."",
  ""linkedEnvironmentMetadata"": {
    ""baseLanguage"": {language},
    ""currency"": { ""code"": ""{currency}"" },
    ""templates"": []
  }
}";

    // PPAC /environmentmanagement/provisioning/environments/{environmentId}/link body.
    // Adds Dataverse to an environment that was provisioned without it.
    private const string LinkDataverseBody =
@"{
  ""baseLanguage"": {language},
  ""currency"": { ""code"": ""{currency}"" },
  ""templates"": []
}";

    public static IReadOnlyList<ApiOperation> Operations { get; } = new List<ApiOperation>
    {
        // ----- Environments -----
        new("Environments", "List environments", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments?api-version=2021-04-01&$expand=properties/billingPolicy",
            ScopePowerApps, null, "All environments visible to the caller."),

        // NOTE: "Get environment" must use /scopes/admin/environments/{id} — the bare
        // /environments/{id} variant 404s for tenant-admin callers even though the LIST
        // /environments works without /scopes/admin/. Same pattern as the working
        // "Environment capacity (expand on env GET)" row below.
        new("Environments", "Get environment", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}?api-version=2021-04-01&$expand=properties/billingPolicy",
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

        // NOTE: the BAP "/scopes/admin/environments/{id}/backups" GET returns 404 — there is
        // no list-backups route on api.bap.microsoft.com. Use the PPAC equivalent instead:
        // PpacGeneratedCatalog "Environment Backups - Get Environment Backups" ->
        // /environmentmanagement/environments/{environmentId}/backups?api-version=2022-03-01-preview
        // The BAP POST ("Backup environment") still exists for create — only the GET is bogus.

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

        // NOTE: legacy BAP "/locations/{location}/environmentTemplates" returns 404 NotFound — no such
        // route exists on api.bap.microsoft.com per Microsoft Docs. Templates now live under PPAC:
        // see PpacGeneratedCatalog "Get Templates By Location" -> /environmentmanagement/provisioning/locations/{location}/templates.

        // Tenant settings: drop the legacy "/scopes/admin/" prefix (404s) AND use POST, not GET.
        // "listTenantSettings" / "updateTenantSettings" are ARM-style list-action verbs (same
        // pattern as listKeys / listSecrets) — the action name is in the path, the HTTP verb is
        // always POST with a (possibly empty) JSON body. Verified empirically across api-versions
        // 2020-10-01, 2021-04-01, and 2023-06-01: POST returns 200, GET returns 404. The PowerShell
        // cmdlet Get-TenantSettings wraps exactly this POST.
        new("Tenant", "Get tenant settings", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/listTenantSettings?api-version=2020-10-01",
            ScopePowerApps, "{}", "Tenant-wide Power Platform settings (ARM-style POST list-action; empty body returns full settings JSON)."),

        new("Tenant", "Update tenant settings", "POST",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/updateTenantSettings?api-version=2020-10-01",
            ScopePowerApps, TenantSettingsBody, "Patch tenant settings (only set fields you want to change)."),

        // ----- Capacity -----
        // NOTE: legacy BAP "/scopes/admin/tenant/capacity" route returns 404 NotFound — superseded by
        // PPAC "/licensing/tenantCapacity" (see PpacGeneratedCatalog "Get Tenant Capacity Details").
        // Same story for the legacy per-environment "/scopes/admin/environments/{id}/capacity" route —
        // it 404s; per the Microsoft "daily capacity report" tutorial and the PowerShell
        // Get-AdminPowerAppEnvironment -Capacity flag, capacity is returned inline via
        // $expand=properties.capacity on the normal environment GET.

        new("Capacity", "Environment capacity (expand on env GET)", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments/{environmentId}?api-version=2021-04-01&$expand=properties.capacity",
            ScopePowerApps, null,
            "Storage breakdown for one environment. Capacity is returned inside properties.capacity (database/file/log entries with actualConsumption + capacityUnit). The legacy /capacity sub-route returns 404.",
            ApiSurface.Bap, new[] { EnvParam }),

        new("Capacity", "All environments with capacity (expand)", "GET",
            "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2021-04-01&$expand=properties.capacity",
            ScopePowerApps, null,
            "List every admin-visible environment with properties.capacity inlined. Mirrors PowerShell Get-AdminPowerAppEnvironment -Capacity."),

        // ----- DLP Policies -----
        new("DLP", "List DLP policies", "GET",
            "https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01",
            ScopePowerApps, null, "Tenant Data Loss Prevention policies."),

        // NOTE: the test-rig warmup seeds {policyId} from the BillingPolicies list — a
        // billing-policy GUID is not a DLP-policy GUID, so the coverage matrix 404s here.
        // The URL itself is correct; supply a real DLP policyId from "List DLP policies".
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
    public static IReadOnlyList<ApiOperation> PpacOperations { get; } = BuildPpacOperations();

    private static IReadOnlyList<ApiOperation> BuildPpacOperations()
    {
        var list = new List<ApiOperation>(PpacGeneratedCatalog.Operations);
        // ----- Manual additions: routes the public docs DO list under api.powerplatform.com but
        //       which the scraped PpacGeneratedCatalog (api-version=2022-03-01-preview) misses.
        //       Provisioning is published under api-version=2024-10-01, so we add it by hand.

        // ----- Environment Provisioning (api-version=2024-10-01) -----
        // Six routes from Learn /environmentmanagement/environment-provisioning/*.
        // Verified live by VerseOps.SdkTests.EnvironmentProvisioningTests (4 GETs pass; the two
        // mutating routes are gated behind opt-in env vars in the test rig).
        const string ProvVer = "api-version=2024-10-01";

        list.Add(new ApiOperation(
            "Environment management",
            "Get Provisioning Locations",
            "GET",
            $"https://api.powerplatform.com/environmentmanagement/provisioning/locations?{ProvVer}",
            ScopePpac,
            null,
            "PPAC: GET https://api.powerplatform.com/environmentmanagement/provisioning/locations  |  Docs: https://learn.microsoft.com/en-us/rest/api/power-platform/environmentmanagement/environment-provisioning/get-locations",
            ApiSurface.Ppac,
            SubCategory: "Environment Provisioning"));

        list.Add(new ApiOperation(
            "Environment management",
            "Get Currencies By Location",
            "GET",
            $"https://api.powerplatform.com/environmentmanagement/provisioning/locations/{{location}}/currencies?{ProvVer}",
            ScopePpac,
            null,
            "PPAC: GET https://api.powerplatform.com/environmentmanagement/provisioning/locations/{location}/currencies  |  Docs: https://learn.microsoft.com/en-us/rest/api/power-platform/environmentmanagement/environment-provisioning/get-currencies-by-location",
            ApiSurface.Ppac,
            new[] { LocationParam },
            SubCategory: "Environment Provisioning"));

        list.Add(new ApiOperation(
            "Environment management",
            "Get Languages By Location",
            "GET",
            $"https://api.powerplatform.com/environmentmanagement/provisioning/locations/{{location}}/languages?{ProvVer}",
            ScopePpac,
            null,
            "PPAC: GET https://api.powerplatform.com/environmentmanagement/provisioning/locations/{location}/languages  |  Docs: https://learn.microsoft.com/en-us/rest/api/power-platform/environmentmanagement/environment-provisioning/get-languages-by-location",
            ApiSurface.Ppac,
            new[] { LocationParam },
            SubCategory: "Environment Provisioning"));

        list.Add(new ApiOperation(
            "Environment management",
            "Get Templates By Location",
            "GET",
            $"https://api.powerplatform.com/environmentmanagement/provisioning/locations/{{location}}/templates?{ProvVer}",
            ScopePpac,
            null,
            "PPAC: GET https://api.powerplatform.com/environmentmanagement/provisioning/locations/{location}/templates  |  Docs: https://learn.microsoft.com/en-us/rest/api/power-platform/environmentmanagement/environment-provisioning/get-templates-by-location",
            ApiSurface.Ppac,
            new[] { LocationParam },
            SubCategory: "Environment Provisioning"));

        list.Add(new ApiOperation(
            "Environment management",
            "Provision New Environment",
            "POST",
            $"https://api.powerplatform.com/environmentmanagement/provisioning/create?{ProvVer}",
            ScopePpac,
            ProvisionCreateBody,
            "PPAC: POST https://api.powerplatform.com/environmentmanagement/provisioning/create  |  Body: CreateEnvironmentRequest. Required: displayName + environmentSku. For a Dataverse-backed sandbox include databaseType=CommonDataService + linkedEnvironmentMetadata{baseLanguage, currency.code, templates}. Returns 201 sync or 202 async (operation-location header).  |  Docs: https://learn.microsoft.com/en-us/rest/api/power-platform/environmentmanagement/environment-provisioning/provision-new-environment",
            ApiSurface.Ppac,
            new[] { DisplayNameParam, SkuParam, LocationParam, CurrencyParam, LanguageParam },
            SubCategory: "Environment Provisioning"));

        list.Add(new ApiOperation(
            "Environment management",
            "Link Dataverse Database To Environment",
            "PATCH",
            $"https://api.powerplatform.com/environmentmanagement/provisioning/environments/{{environmentId}}/link?{ProvVer}",
            ScopePpac,
            LinkDataverseBody,
            "PPAC: PATCH https://api.powerplatform.com/environmentmanagement/provisioning/environments/{environmentId}/link  |  Body: linkedEnvironmentMetadata { baseLanguage, currency.code, templates[] }. Attaches a Dataverse org to an environment that was created without one. Returns 202 + operation-location.  |  Docs: https://learn.microsoft.com/en-us/rest/api/power-platform/environmentmanagement/environment-provisioning/link-dataverse",
            ApiSurface.Ppac,
            new[] { EnvParam, CurrencyParam, LanguageParam },
            SubCategory: "Environment Provisioning"));

        return list;
    }

    /// <summary>Returns operations filtered by surface (BAP or PPAC).</summary>
    public static IEnumerable<ApiOperation> ForSurface(ApiSurface surface)
    {
        return surface == ApiSurface.Ppac ? PpacOperations : Operations;
    }
}
