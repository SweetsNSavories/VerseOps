using System.Text.Json;
using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// Persisted form of <see cref="DataverseEnvClient.EnvDetails"/> for the
/// per-env drill-down cache (table <c>gov_env_details</c>). Optimised for
/// round-trip via JSON:
///
///   * Solutions store only the asset IDs in their Apps/Flows/Agents
///     buckets (not the live <see cref="AssetRow"/> instances). On
///     hydration we re-resolve by AssetId against the env's currently
///     loaded <c>row.Assets</c>, so the same INPC instance is shared
///     between the flat-view grid and the per-solution buckets — no
///     duplicate copies, no stale state.
///   * Power Pages and Users are pure value objects so we round-trip
///     them directly (POCO serialization).
///   * Per-asset enrichments stamped by the canvas / workflow / appmodule
///     loaders (Status / IsPremium / DlpStatus / SolutionName / IsManaged)
///     are captured separately so the second-and-later expand can re-stamp
///     them onto the live rows without re-hitting Dataverse.
///
/// Schema-less by design: extra fields added to this DTO in future versions
/// just get ignored by older readers; missing fields default to null and
/// the cache silently degrades.
/// </summary>
public sealed class EnvDetailsSnapshot
{
    public DateTime SyncedUtc { get; set; }

    public List<SolutionSnapshot> Solutions { get; set; } = new();
    public List<PowerPageRow> PowerPages { get; set; } = new();
    public List<UserGroupRow> UsersAndGroups { get; set; } = new();
    public List<AssetEnrichment> Enrichments { get; set; } = new();

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = false,
        // INPC backing fields look like properties to STJ; only serialise
        // the public ones. Default behaviour matches that — explicit for
        // future-proofing.
        IncludeFields = false,
    };

    /// <summary>
    /// Build a snapshot from a live <see cref="DataverseEnvClient.EnvDetails"/>
    /// + the env's current asset list. <paramref name="envAssets"/> is the
    /// source of truth for the per-asset enrichments (the loaders stamp them
    /// in-place before the EnvDetails is returned).
    /// </summary>
    public static EnvDetailsSnapshot Capture(
        DataverseEnvClient.EnvDetails details,
        IReadOnlyList<AssetRow> envAssets,
        DateTime syncedUtc)
    {
        var snap = new EnvDetailsSnapshot { SyncedUtc = syncedUtc };

        foreach (var s in details.Solutions)
        {
            snap.Solutions.Add(new SolutionSnapshot
            {
                Name        = s.Name,
                UniqueName  = s.UniqueName,
                IsManaged   = s.IsManaged,
                Publisher   = s.Publisher,
                Version     = s.Version,
                SolutionId  = s.SolutionId,
                EnvId       = s.EnvId,
                CreatedUtc  = s.CreatedUtc,
                ModifiedUtc = s.ModifiedUtc,
                RawJson     = s.RawJson,
                AppIds      = s.Apps.Select(a => a.AssetId).ToList(),
                FlowIds     = s.Flows.Select(a => a.AssetId).ToList(),
                AgentIds    = s.Agents.Select(a => a.AssetId).ToList(),
            });
        }

        snap.PowerPages.AddRange(details.PowerPages);
        snap.UsersAndGroups.AddRange(details.UsersAndGroups);

        // Capture every per-asset enrichment touched by the Dataverse loaders.
        // We only store rows that have at least one non-default value so the
        // payload stays small on tenants with thousands of assets.
        foreach (var a in envAssets)
        {
            if (string.IsNullOrEmpty(a.AssetId)) continue;
            if (a.Status == null && a.IsPremium == null && a.DlpStatus == null
                && a.SolutionName == null && a.IsManaged == null)
                continue;
            snap.Enrichments.Add(new AssetEnrichment
            {
                AssetId      = a.AssetId,
                Status       = a.Status,
                IsPremium    = a.IsPremium,
                DlpStatus    = a.DlpStatus,
                SolutionName = a.SolutionName,
                IsManaged    = a.IsManaged,
            });
        }

        return snap;
    }

    /// <summary>
    /// Serialise to the JSON form persisted in <c>gov_env_details.payload_json</c>.
    /// </summary>
    public string Serialize() => JsonSerializer.Serialize(this, s_jsonOpts);

    /// <summary>
    /// Inverse of <see cref="Serialize"/>. Returns <c>null</c> on parse
    /// failure so the caller can transparently fall back to a live fetch.
    /// </summary>
    public static EnvDetailsSnapshot? Deserialize(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<EnvDetailsSnapshot>(payloadJson, s_jsonOpts); }
        catch { return null; }
    }

    /// <summary>
    /// Materialise this snapshot back into a live <see cref="DataverseEnvClient.EnvDetails"/>,
    /// re-bucketing solutions against <paramref name="envAssets"/> by AssetId
    /// and stamping the saved enrichments back onto each AssetRow in-place.
    /// Apps/flows/agents whose AssetIds no longer exist in the env (asset
    /// deleted between snapshot + hydrate) are silently skipped.
    /// </summary>
    public DataverseEnvClient.EnvDetails Hydrate(IReadOnlyList<AssetRow> envAssets)
    {
        var byId = new Dictionary<string, AssetRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in envAssets)
            if (!string.IsNullOrEmpty(a.AssetId))
                byId[a.AssetId] = a;

        // Stamp enrichments first so the rebucketed solutions inherit the
        // freshly-restored SolutionName / IsManaged on each AssetRow.
        foreach (var e in Enrichments)
        {
            if (!byId.TryGetValue(e.AssetId, out var row)) continue;
            if (e.Status       != null) row.Status       = e.Status;
            if (e.IsPremium.HasValue)   row.IsPremium    = e.IsPremium;
            if (e.DlpStatus    != null) row.DlpStatus    = e.DlpStatus;
            if (e.SolutionName != null) row.SolutionName = e.SolutionName;
            if (e.IsManaged.HasValue)   row.IsManaged    = e.IsManaged;
        }

        var solutions = new List<SolutionGroup>(Solutions.Count);
        foreach (var s in Solutions)
        {
            solutions.Add(new SolutionGroup
            {
                Name        = s.Name,
                UniqueName  = s.UniqueName,
                IsManaged   = s.IsManaged,
                Publisher   = s.Publisher,
                Version     = s.Version,
                SolutionId  = s.SolutionId,
                EnvId       = s.EnvId,
                CreatedUtc  = s.CreatedUtc,
                ModifiedUtc = s.ModifiedUtc,
                RawJson     = s.RawJson,
                Apps        = ResolveAssets(s.AppIds,   byId),
                Flows       = ResolveAssets(s.FlowIds,  byId),
                Agents      = ResolveAssets(s.AgentIds, byId),
            });
        }

        return new DataverseEnvClient.EnvDetails(solutions, PowerPages, UsersAndGroups);
    }

    private static List<AssetRow> ResolveAssets(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, AssetRow> byId)
    {
        var list = new List<AssetRow>(ids.Count);
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var row))
                list.Add(row);
        return list;
    }
}

/// <summary>JSON-friendly mirror of <see cref="SolutionGroup"/>.</summary>
public sealed class SolutionSnapshot
{
    public string Name { get; set; } = string.Empty;
    public string? UniqueName { get; set; }
    public bool IsManaged { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? SolutionId { get; set; }
    public string? EnvId { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public string? RawJson { get; set; }
    public List<string> AppIds   { get; set; } = new();
    public List<string> FlowIds  { get; set; } = new();
    public List<string> AgentIds { get; set; } = new();
}

/// <summary>
/// Per-asset enrichments stamped by the Dataverse loaders, captured so a
/// hydrate doesn't need to re-hit canvasapps / workflows / appmodules /
/// solutions to restore Status / IsPremium / DlpStatus / SolutionName / IsManaged.
/// </summary>
public sealed class AssetEnrichment
{
    public string AssetId { get; set; } = string.Empty;
    public string? Status { get; set; }
    public bool? IsPremium { get; set; }
    public string? DlpStatus { get; set; }
    public string? SolutionName { get; set; }
    public bool? IsManaged { get; set; }
}
