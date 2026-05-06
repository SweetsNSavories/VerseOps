using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using VerseOps.App.Auth;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// Pulls per-user license assignments from Microsoft Graph and decodes the
/// SKU GUIDs into friendly names using the tenant's <c>subscribedSkus</c>
/// catalog. One instance covers the whole grid load — tenant catalog +
/// users are paged once per call. Per-user lookup is then O(1) via
/// <see cref="LicensesByUpn"/>.
///
/// Scope: <c>https://graph.microsoft.com/.default</c>. The signed-in user
/// needs at least <c>User.Read.All</c> + <c>Directory.Read.All</c>; any
/// admin role (Global Reader, User Admin, etc.) suffices. On 403 we
/// silently degrade — the License column simply stays blank rather than
/// blocking the rest of the env drill-down.
/// </summary>
public sealed class GraphLicenseClient
{
    private const string GraphScope   = "https://graph.microsoft.com/.default";
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0/";

    private readonly AuthService _auth;

    /// <summary>Map of UPN (lower-cased) → list of friendly SKU names. Populated by <see cref="LoadAsync"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LicensesByUpn { get; private set; }
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Map of Azure AD object id (GUID, lower-cased) → friendly owner label
    /// "DisplayName (UPN)". Populated alongside <see cref="LicensesByUpn"/>
    /// during <see cref="LoadAsync"/> so resolving asset/flow owner GUIDs to
    /// human-readable names is free after the directory pull.
    /// </summary>
    public IReadOnlyDictionary<string, string> UserLabelsById { get; private set; }
        = new Dictionary<string, string>();

    /// <summary>Optional warning surfaced to the caller (e.g. "Graph returned 403 — License column blank").</summary>
    public string? Warning { get; private set; }

    public GraphLicenseClient(AuthService auth) => _auth = auth;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await _auth.GetTokenAsync(GraphScope, ct).ConfigureAwait(false);

            using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                BaseAddress = new Uri(GraphBaseUrl),
                Timeout = TimeSpan.FromSeconds(60)
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // ---- 1. SKU catalog -------------------------------------------------
            // Returns (id, skuPartNumber). skuPartNumber is the upper-snake-case
            // identifier humans recognize ("ENTERPRISEPACK" / "POWER_BI_PRO").
            var skuMap = new Dictionary<Guid, string>();
            using (var resp = await http.GetAsync("subscribedSkus?$select=skuId,skuPartNumber", ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    Warning = $"Graph subscribedSkus returned {(int)resp.StatusCode} — License column will be blank.";
                    return;
                }
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in arr.EnumerateArray())
                    {
                        if (!s.TryGetProperty("skuId", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                        if (!Guid.TryParse(idEl.GetString(), out var id)) continue;
                        var name = s.TryGetProperty("skuPartNumber", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? id.ToString()
                            : id.ToString();
                        skuMap[id] = name;
                    }
                }
            }

            // ---- 2. Users with their assigned licenses --------------------------
            // Paged via @odata.nextLink. $top is capped at 999 by Graph for users.
            var byUpn = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var byId  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var url = "users?$select=id,userPrincipalName,displayName,assignedLicenses&$top=999";
            while (!string.IsNullOrEmpty(url))
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Warning = $"Graph users returned {(int)resp.StatusCode} — License column may be partial.";
                    break;
                }
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("value", out var users) && users.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in users.EnumerateArray())
                    {
                        var upn = u.TryGetProperty("userPrincipalName", out var upnEl) && upnEl.ValueKind == JsonValueKind.String
                            ? upnEl.GetString() : null;
                        var displayName = u.TryGetProperty("displayName", out var dnEl) && dnEl.ValueKind == JsonValueKind.String
                            ? dnEl.GetString() : null;
                        var id = u.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() : null;

                        // Build the userId → label map even for licenseless users
                        // so owner-GUID resolution still works on accounts with
                        // no SKUs assigned (service principals are added below
                        // by walking servicePrincipals).
                        if (!string.IsNullOrEmpty(id))
                        {
                            var label = (!string.IsNullOrEmpty(displayName), !string.IsNullOrEmpty(upn)) switch
                            {
                                (true,  true)  => $"{displayName} ({upn})",
                                (true,  false) => displayName!,
                                (false, true)  => upn!,
                                _              => id!
                            };
                            byId[id!] = label;
                        }

                        if (string.IsNullOrEmpty(upn)) continue;

                        var names = new List<string>();
                        if (u.TryGetProperty("assignedLicenses", out var lics) && lics.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var lic in lics.EnumerateArray())
                            {
                                if (!lic.TryGetProperty("skuId", out var sidEl) || sidEl.ValueKind != JsonValueKind.String)
                                    continue;
                                if (!Guid.TryParse(sidEl.GetString(), out var sid)) continue;
                                names.Add(skuMap.TryGetValue(sid, out var friendly) ? friendly : sid.ToString());
                            }
                        }
                        byUpn[upn] = names;
                    }
                }

                url = root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString()!
                    : string.Empty;
                // nextLink is absolute; HttpClient will use it as-is once we strip
                // the BaseAddress so the relative-URL handling doesn't kick in.
                if (!string.IsNullOrEmpty(url) && url.StartsWith(GraphBaseUrl, StringComparison.OrdinalIgnoreCase))
                    url = url.Substring(GraphBaseUrl.Length);
            }
            LicensesByUpn = byUpn;

            // ---- 3. Service principals (so SP-owned apps/flows resolve too) -----
            // Best-effort: 403 here just means non-user owners stay as GUIDs.
            var spUrl = "servicePrincipals?$select=id,displayName,appId&$top=999";
            try
            {
                while (!string.IsNullOrEmpty(spUrl))
                {
                    using var resp = await http.GetAsync(spUrl, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) break;
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sp in arr.EnumerateArray())
                        {
                            var id = sp.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                            var dn = sp.TryGetProperty("displayName", out var dnEl) && dnEl.ValueKind == JsonValueKind.String ? dnEl.GetString() : null;
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(dn))
                                byId.TryAdd(id!, $"{dn} (service principal)");
                        }
                    }
                    spUrl = root.TryGetProperty("@odata.nextLink", out var nx) && nx.ValueKind == JsonValueKind.String
                        ? nx.GetString()! : string.Empty;
                    if (!string.IsNullOrEmpty(spUrl) && spUrl.StartsWith(GraphBaseUrl, StringComparison.OrdinalIgnoreCase))
                        spUrl = spUrl.Substring(GraphBaseUrl.Length);
                }
            }
            catch { /* SP enumeration is bonus — never block the license fetch */ }

            UserLabelsById = byId;
        }
        catch (Exception ex)
        {
            Warning = $"Graph license lookup failed ({ex.GetType().Name}: {ex.Message}).";
            LicensesByUpn = new Dictionary<string, IReadOnlyList<string>>();
        }
    }

    /// <summary>
    /// Build the compact one-line summary shown in the License column
    /// ("ENTERPRISEPACK +2") and the full newline-separated tooltip text.
    /// </summary>
    public static (string? compact, string? full) Format(IReadOnlyList<string> licenses)
    {
        if (licenses == null || licenses.Count == 0) return ("(no licenses)", "(no Microsoft 365 licenses assigned)");
        var ordered = licenses.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var compact = ordered.Count == 1 ? ordered[0] : $"{ordered[0]} +{ordered.Count - 1}";
        var full = string.Join("\n", ordered);
        return (compact, full);
    }

    /// <summary>
    /// Calls Microsoft Graph <c>POST /me/checkMemberGroups</c> with the
    /// distinct list of env security-group ids and returns the subset the
    /// signed-in user is a transitive member of. A single round-trip is
    /// enough — the endpoint accepts up to 20 group ids per call (we
    /// chunk if more). Uses the same scope as the license fetch.
    /// </summary>
    /// <returns>
    /// Set of group ids the user belongs to. On any failure we return an
    /// empty set and set <see cref="Warning"/> — the toggle then simply
    /// hides every env (the user can fall back to the WhoAmI Dataverse
    /// path by toggling off; the env id / instance URL columns still copy).
    /// </returns>
    public async Task<HashSet<string>> CheckSecurityGroupMembershipAsync(
        IEnumerable<string> groupIds,
        CancellationToken ct = default)
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = groupIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return matched;

        try
        {
            var token = await _auth.GetTokenAsync(GraphScope, ct).ConfigureAwait(false);
            using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                BaseAddress = new Uri(GraphBaseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Graph caps checkMemberGroups at 20 ids per call.
            const int Chunk = 20;
            for (int i = 0; i < distinct.Count; i += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                var slice = distinct.Skip(i).Take(Chunk).ToList();
                var body = new { groupIds = slice };
                var json = JsonSerializer.Serialize(body);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                using var resp = await http.PostAsync("me/checkMemberGroups", content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Warning = $"Graph checkMemberGroups returned {(int)resp.StatusCode} — security-group filter degraded.";
                    return matched;
                }
                var respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respJson);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String)
                            matched.Add(el.GetString()!);
            }
        }
        catch (Exception ex)
        {
            Warning = $"Graph membership check failed ({ex.GetType().Name}: {ex.Message}).";
        }
        return matched;
    }

    /// <summary>
    /// Tenant-wide license consumption rollup, derived from the cached
    /// <see cref="LicensesByUpn"/> map (so this is free after
    /// <see cref="LoadAsync"/> has run). The result is sorted descending
    /// by user count so the busiest SKUs appear first in the tile drawer.
    /// </summary>
    public IReadOnlyList<(string Sku, int UserCount)> GetSkuConsumption()
    {
        return LicensesByUpn
            .SelectMany(kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Sku: g.Key, UserCount: g.Count()))
            .OrderByDescending(t => t.UserCount)
            .ThenBy(t => t.Sku, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Total distinct users with at least one license assigned.</summary>
    public int LicensedUserCount => LicensesByUpn.Count(kv => kv.Value.Count > 0);
}
