using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using VerseOps.App.Auth;
using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// Per-environment Dataverse Web API client. Owns its own short-lived
/// <see cref="HttpClient"/> + diagnostics handler (DelegatingHandler instances
/// can only be wired up once, see <see cref="BapCapacityClient"/> for the same
/// pattern). One instance per env-load call.
///
/// Talks to <c>{instanceUrl}/api/data/v9.2</c> using a token acquired with
/// scope <c>{instanceUrl}/.default</c> (so the user must hold a Dataverse
/// security role on the target env — System Administrator is the simplest;
/// System Customizer is enough for solutions / pages reads).
///
/// All three loaders return empty lists on transport failure so the caller
/// can show a banner and let the user move on. Auth failures bubble up
/// because they're typically actionable (sign in as the right user).
///
/// Each loader captures the raw Dataverse JSON for every row alongside the
/// typed mapping, so the UI's "Metadata Inspector" pop-out can show the
/// underlying record exactly as Dataverse returned it (PCF parity).
/// </summary>
public sealed class DataverseEnvClient
{
    private readonly AuthService _auth;
    private readonly HttpDiagnosticsHandler _diagnostics;

    public DataverseEnvClient(AuthService auth, HttpDiagnosticsHandler diagnostics)
    {
        _auth = auth;
        _diagnostics = diagnostics;
    }

    /// <summary>Pulls solutions, Power Pages, and users for one env in parallel.</summary>
    public async Task<EnvDetails> LoadAllAsync(
        string envId,
        string instanceUrl,
        IReadOnlyList<AssetRow> envAssets,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(instanceUrl))
            throw new InvalidOperationException("Environment has no Dataverse instance URL (non-Dataverse env).");

        // Normalize to a clean origin (no trailing slash, no path).
        var origin = new Uri(instanceUrl).GetLeftPart(UriPartial.Authority);
        var scope  = origin + "/.default";
        var token  = await _auth.GetTokenAsync(scope, ct).ConfigureAwait(false);

        // Fresh per-call pipeline. See BapCapacityClient for the rationale on
        // why we don't reuse the caller's diagnostics handler instance.
        var ownDiag = new HttpDiagnosticsHandler { ShouldDumpFailure = _diagnostics.ShouldDumpFailure };
        var inner   = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        ownDiag.InnerHandler = inner;
        using var http = new HttpClient(ownDiag, disposeHandler: true)
        {
            BaseAddress = new Uri(origin + "/api/data/v9.2/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // OData-MaxVersion is required by the Dataverse Web API; FormattedValue
        // annotation gives us nice display strings for option-set fields.
        http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        http.DefaultRequestHeaders.Add("OData-Version", "4.0");
        http.DefaultRequestHeaders.Add(
            "Prefer",
            "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");

        // Run the three loads concurrently. Each is independent and any one
        // can fail (e.g. mspp_website table missing on a non-Pages env) without
        // blocking the others. Failed sub-loads return empty lists.
        var solutionsTask = SafeAsync(() => LoadSolutionsAsync(http, envId, envAssets, ct));
        var pagesTask     = SafeAsync(() => LoadPowerPagesAsync(http, envId, ct));
        var usersTask     = SafeAsync(() => LoadUsersAsync(http, origin, ct));
        await Task.WhenAll(solutionsTask, pagesTask, usersTask).ConfigureAwait(false);

        return new EnvDetails(
            solutionsTask.Result ?? Array.Empty<SolutionGroup>(),
            pagesTask.Result     ?? Array.Empty<PowerPageRow>(),
            usersTask.Result     ?? Array.Empty<UserGroupRow>());
    }

    /// <summary>
    /// Lightweight membership probe used by the "Only my environments" toggle.
    /// Calls Dataverse <c>WhoAmI</c> against the env's instance URL using the
    /// signed-in user's token. Returns <c>true</c> on HTTP 200, <c>false</c>
    /// on 401/403/404 (token works but the user has no <c>systemuser</c> on
    /// this env, or the env has no Dataverse at all). Any other failure is
    /// treated as <c>null</c> so the caller can decide to retry vs. mark the
    /// row "unknown".
    ///
    /// This is intentionally a tiny one-shot HTTP call — no parsing, no
    /// caching, no retries — because it's invoked in parallel across every
    /// env in the tenant the moment the toggle is flipped on.
    /// </summary>
    public async Task<bool?> CheckCurrentUserMembershipAsync(string instanceUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(instanceUrl)) return false;
        try
        {
            var origin = new Uri(instanceUrl).GetLeftPart(UriPartial.Authority);
            var scope  = origin + "/.default";
            var token  = await _auth.GetTokenAsync(scope, ct).ConfigureAwait(false);

            // Fresh per-call pipeline (DelegatingHandler.InnerHandler is
            // single-assignment; same pattern as LoadAllAsync above).
            var ownDiag = new HttpDiagnosticsHandler { ShouldDumpFailure = _diagnostics.ShouldDumpFailure };
            var inner   = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
            ownDiag.InnerHandler = inner;
            using var http = new HttpClient(ownDiag, disposeHandler: true)
            {
                BaseAddress = new Uri(origin + "/api/data/v9.2/"),
                Timeout = TimeSpan.FromSeconds(20)
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
            http.DefaultRequestHeaders.Add("OData-Version", "4.0");

            using var resp = await http.GetAsync("WhoAmI", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return true;
            // 401/403 = signed-in user is not a Dataverse user on this env.
            // 404 = WhoAmI route missing (very old envs / non-Dataverse env).
            if (resp.StatusCode is HttpStatusCode.Unauthorized
                                or HttpStatusCode.Forbidden
                                or HttpStatusCode.NotFound)
                return false;
            return null;
        }
        catch
        {
            // Network/auth fault — leave the row's membership as "unknown" so
            // a later retry can refine it without surfacing scary errors here.
            return null;
        }
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T>> work) where T : class
    {
        try { return await work().ConfigureAwait(false); }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // Solutions: pull every visible solution + every solutioncomponent for
    // (canvasapp / cloudflow / modeldrivenapp / agent) component types,
    // then bucket the env's Inventory-API assets by their owning solution.
    // Component types reference (Dataverse `componenttype` enum):
    //   29  = Workflow (cloud flows + classic workflows)
    //   61  = ModelDrivenApp / AppModule
    //   80  = CanvasApp
    //   300 = Bot / Copilot Agent
    // We DO NOT filter empty solutions — the screenshot shows every
    // installed solution (Access Team, Activities, etc.) even if it
    // contains no apps/flows/agents.
    // ------------------------------------------------------------------
    private async Task<IReadOnlyList<SolutionGroup>> LoadSolutionsAsync(
        HttpClient http,
        string envId,
        IReadOnlyList<AssetRow> envAssets,
        CancellationToken ct)
    {
        var solUrl = "solutions"
            + "?$select=solutionid,uniquename,friendlyname,version,ismanaged,createdon,modifiedon,_publisherid_value"
            + "&$expand=publisherid($select=friendlyname,uniquename)"
            + "&$filter=isvisible eq true"
            + "&$orderby=friendlyname asc"
            + "&$top=500";
        var solRows = await GetRowsAsync(http, solUrl, ct).ConfigureAwait(false);

        // Pull every component for the types we care about. componenttype is an
        // option-set so we filter with In(). $top=5000 covers most tenants.
        var compUrl = "solutioncomponents"
            + "?$select=componenttype,objectid,_solutionid_value"
            + "&$filter=Microsoft.Dynamics.CRM.In(PropertyName='componenttype',PropertyValues=['29','61','80','300'])"
            + "&$top=5000";
        var compRows = await GetRowsAsync(http, compUrl, ct).ConfigureAwait(false);

        // Build asset_id -> AssetRow lookup once. AssetRow.AssetId is the resource
        // Guid returned by the Inventory API; that's the same value as
        // solutioncomponent.objectid for canvas/model-driven/agent components.
        var assetById = new Dictionary<string, AssetRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in envAssets)
        {
            if (!string.IsNullOrEmpty(a.AssetId))
                assetById[a.AssetId] = a;
        }

        // Bucket components per solution.
        var bySolutionId = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in compRows)
        {
            var sid = ReadString(c, "_solutionid_value");
            if (string.IsNullOrEmpty(sid)) continue;
            if (!bySolutionId.TryGetValue(sid, out var bucket))
                bySolutionId[sid] = bucket = new List<JsonElement>();
            bucket.Add(c);
        }

        var result = new List<SolutionGroup>(solRows.Count);
        var assignedAssetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in solRows)
        {
            var solutionId = ReadString(s, "solutionid");
            if (string.IsNullOrEmpty(solutionId)) continue;
            bySolutionId.TryGetValue(solutionId, out var comps);
            comps ??= new List<JsonElement>();

            var apps   = new List<AssetRow>();
            var flows  = new List<AssetRow>();
            var agents = new List<AssetRow>();

            foreach (var c in comps)
            {
                var objId = ReadString(c, "objectid");
                if (string.IsNullOrEmpty(objId)) continue;
                if (!assetById.TryGetValue(objId, out var asset)) continue;
                assignedAssetIds.Add(asset.AssetId);
                var ct2 = ReadInt(c, "componenttype");
                switch (ct2)
                {
                    case 29:  flows.Add(asset);  break;
                    case 61:  apps.Add(asset);   break; // ModelDrivenApp
                    case 80:  apps.Add(asset);   break; // CanvasApp
                    case 300: agents.Add(asset); break; // Bot / Agent
                }
            }

            string? publisherFriendly = null, publisherUnique = null;
            if (s.TryGetProperty("publisherid", out var pub) && pub.ValueKind == JsonValueKind.Object)
            {
                publisherFriendly = ReadString(pub, "friendlyname");
                publisherUnique   = ReadString(pub, "uniquename");
            }

            var solutionName = ReadString(s, "friendlyname") ?? ReadString(s, "uniquename") ?? "(unnamed)";

            // Stamp the friendly solution name back onto each asset so the
            // FLAT-view DataGrids can show a "Solution" column without a
            // second lookup. INPC fires so any already-bound row repaints.
            foreach (var asset in apps)   asset.SolutionName = solutionName;
            foreach (var asset in flows)  asset.SolutionName = solutionName;
            foreach (var asset in agents) asset.SolutionName = solutionName;

            result.Add(new SolutionGroup
            {
                Name        = solutionName,
                UniqueName  = ReadString(s, "uniquename"),
                IsManaged   = ReadBool(s, "ismanaged") ?? false,
                Version     = ReadString(s, "version"),
                SolutionId  = solutionId,
                EnvId       = envId,
                Publisher   = publisherFriendly ?? publisherUnique,
                CreatedUtc  = ReadDate(s, "createdon"),
                ModifiedUtc = ReadDate(s, "modifiedon"),
                Apps        = apps,
                Flows       = flows,
                Agents      = agents,
                RawJson     = PrettyPrint(s)
            });
        }

        // Catch-all bucket for any inventory asset that wasn't claimed by a
        // visible solution (orphans in the Default Solution, components in
        // hidden internal solutions, etc.). Always added at the end.
        var orphanApps   = new List<AssetRow>();
        var orphanFlows  = new List<AssetRow>();
        var orphanAgents = new List<AssetRow>();
        foreach (var a in envAssets)
        {
            if (assignedAssetIds.Contains(a.AssetId)) continue;
            if (string.Equals(a.AssetType, "agents", StringComparison.OrdinalIgnoreCase))
                orphanAgents.Add(a);
            else if (a.AssetType is "cloudflows" or "agentflows" or "m365agentflows")
                orphanFlows.Add(a);
            else
                orphanApps.Add(a);
        }
        if (orphanApps.Count + orphanFlows.Count + orphanAgents.Count > 0)
        {
            // Stamp the synthetic name onto orphan assets so the FLAT view
            // shows them as "(unmatched)" instead of empty.
            foreach (var a in orphanApps)   a.SolutionName = "(unmatched)";
            foreach (var a in orphanFlows)  a.SolutionName = "(unmatched)";
            foreach (var a in orphanAgents) a.SolutionName = "(unmatched)";

            result.Add(new SolutionGroup
            {
                Name       = "(unmatched / Default Solution)",
                UniqueName = "Default",
                IsManaged  = false,
                EnvId      = envId,
                Publisher  = "no parent solution detected",
                Apps       = orphanApps,
                Flows      = orphanFlows,
                Agents     = orphanAgents,
                RawJson    = "{ \"_synthetic\": true, \"description\": \"Inventory-API assets not claimed by any visible Dataverse solution.\" }"
            });
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Power Pages: query the modern mspp_website table; gracefully fall
    // back to the legacy adx_website table on envs where Power Pages
    // hasn't been provisioned (the Pages service publishes the new table
    // only after the user creates their first site).
    // ------------------------------------------------------------------
    private async Task<IReadOnlyList<PowerPageRow>> LoadPowerPagesAsync(HttpClient http, string envId, CancellationToken ct)
    {
        var rows = await TryQueryPagesAsync(http, envId,
            "mspp_websites?$select=mspp_websiteid,mspp_name,mspp_primarydomainname,mspp_websitetype,statecode,createdon,modifiedon&$top=200",
            "mspp_websiteid", "mspp_name", "mspp_primarydomainname", "mspp_websitetype", ct).ConfigureAwait(false);
        if (rows.Count > 0) return rows;

        // Legacy portals
        return await TryQueryPagesAsync(http, envId,
            "adx_websites?$select=adx_websiteid,adx_name,adx_primarydomainname,statecode,createdon,modifiedon&$top=200",
            "adx_websiteid", "adx_name", "adx_primarydomainname", null, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PowerPageRow>> TryQueryPagesAsync(
        HttpClient http, string envId, string url,
        string idField, string nameField, string domainField, string? typeField,
        CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<PowerPageRow>();
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<PowerPageRow>();

            var list = new List<PowerPageRow>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
            {
                list.Add(new PowerPageRow
                {
                    Name          = ReadString(el, nameField) ?? "(unnamed site)",
                    WebsiteId     = ReadString(el, idField),
                    PrimaryDomain = ReadString(el, domainField),
                    WebsiteType   = typeField is null ? "Legacy Portal"
                                    : ReadFormatted(el, typeField) ?? ReadString(el, typeField),
                    Status        = ReadFormatted(el, "statecode") ?? ReadString(el, "statecode"),
                    CreatedUtc    = ReadDate(el, "createdon"),
                    ModifiedUtc   = ReadDate(el, "modifiedon"),
                    EnvId         = envId,
                    RawJson       = PrettyPrint(el)
                });
            }
            return list;
        }
        catch { return Array.Empty<PowerPageRow>(); }
    }

    // ------------------------------------------------------------------
    // Users: pull non-disabled systemusers, classify by accessmode +
    // System Administrator role membership for the Admin Status badge.
    // ------------------------------------------------------------------
    private async Task<IReadOnlyList<UserGroupRow>> LoadUsersAsync(HttpClient http, string instanceUrl, CancellationToken ct)
    {
        // Top 500 keeps the payload manageable on big tenants. The grid is
        // still a drill-down surface, not a tenant-wide audit.
        var url = "systemusers"
            + "?$select=systemuserid,fullname,domainname,internalemailaddress,accessmode,createdon,modifiedon"
            + "&$expand=systemuserroles_association($select=name)"
            + "&$filter=isdisabled eq false"
            + "&$orderby=fullname asc"
            + "&$top=500";

        var rows = await GetRowsAsync(http, url, ct).ConfigureAwait(false);
        var list = new List<UserGroupRow>(rows.Count);

        foreach (var u in rows)
        {
            var accessMode = ReadInt(u, "accessmode");
            var access = accessMode switch
            {
                0  => "Standard User",
                1  => "Admin User",
                2  => "Read-Only",
                3  => "Support User",
                4  => "Non-interactive",
                5  => "Delegated Admin",
                18 => "App User",
                _  => "Other"
            };

            bool isAdmin = false;
            if (u.TryGetProperty("systemuserroles_association", out var roles) && roles.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in roles.EnumerateArray())
                {
                    var name = ReadString(r, "name");
                    if (string.Equals(name, "System Administrator", StringComparison.OrdinalIgnoreCase))
                    { isAdmin = true; break; }
                }
            }

            list.Add(new UserGroupRow
            {
                DisplayName    = ReadString(u, "fullname") ?? ReadString(u, "domainname") ?? "(unnamed)",
                Identity       = ReadString(u, "internalemailaddress") ?? ReadString(u, "domainname"),
                SecurityAccess = access,
                AdminStatus    = isAdmin ? "Admin" : "Non-Admin",
                CreatedUtc     = ReadDate(u, "createdon"),
                ModifiedUtc    = ReadDate(u, "modifiedon"),
                SystemUserId   = ReadString(u, "systemuserid"),
                InstanceUrl    = instanceUrl,
                RawJson        = PrettyPrint(u)
            });
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Helpers — JsonElement paging + safe reads.
    // ------------------------------------------------------------------

    /// <summary>
    /// Issue a GET, parse the OData payload, and return every <c>value[i]</c>
    /// element as a list of <see cref="JsonElement"/>. Each element is
    /// <see cref="JsonElement.Clone"/>'d so it survives the document being
    /// disposed.
    /// </summary>
    private static async Task<List<JsonElement>> GetRowsAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<JsonElement>();
        var list = new List<JsonElement>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
            list.Add(el.Clone());
        return list;
    }

    /// <summary>Indented JSON serialization for the metadata inspector dialog.</summary>
    private static string PrettyPrint(JsonElement el)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            el.WriteTo(writer);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? ReadString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string? ReadFormatted(JsonElement el, string name)
        => el.TryGetProperty(name + "@OData.Community.Display.V1.FormattedValue", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static bool? ReadBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _ => (bool?)null
        };
    }

    private static int ReadInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : 0;
    }

    private static DateTime? ReadDate(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : (DateTime?)null;
    }

    public sealed record EnvDetails(
        IReadOnlyList<SolutionGroup> Solutions,
        IReadOnlyList<PowerPageRow> PowerPages,
        IReadOnlyList<UserGroupRow> UsersAndGroups);
}
