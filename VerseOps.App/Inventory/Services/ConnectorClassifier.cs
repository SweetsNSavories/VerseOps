using System.Text.Json;
using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// In-process helpers for classifying Power Platform connectors as Standard
/// vs Premium and evaluating an asset's connector list against a tenant DLP
/// policy snapshot. Pure compute — no I/O — so it's safe to call from any
/// thread inside an enrichment loop.
///
/// What "Standard" means here:
/// the curated Microsoft list of connectors that DO NOT require a per-user
/// premium Power Apps / Power Automate license. Anything else is treated as
/// premium. The list below is conservative — it intentionally errs on the
/// side of saying "Premium" when a connector isn't in our allow list, so the
/// dashboard never falsely tells an admin "you have no premium dependencies"
/// when they actually do.
/// Source: https://learn.microsoft.com/connectors/connector-reference/ —
/// the "Standard connector reference" subset, as of 2026-Q1. Update this list
/// when Microsoft moves a connector between tiers.
/// </summary>
internal static class ConnectorClassifier
{
    /// <summary>
    /// Connector ids known to be in the Microsoft Standard tier. Match key is
    /// the lower-cased <c>shared_*</c> id without environment suffixes.
    /// </summary>
    private static readonly HashSet<string> StandardConnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Office 365 family
        "shared_office365",                 // Outlook
        "shared_office365users",
        "shared_office365groups",
        "shared_outlook",                   // Outlook.com (consumer)
        "shared_outlooktasks",
        "shared_office365video",
        "shared_office365tasks",
        "shared_excelonline",
        "shared_excelonlinebusiness",
        "shared_onenote",
        "shared_onedriveforbusiness",
        "shared_onedrive",
        "shared_sharepointonline",
        "shared_teams",
        "shared_microsoftteams",
        "shared_planner",
        "shared_microsoftforms",
        "shared_microsofttodo",
        "shared_microsoftkaizala",
        "shared_microsoftstream",
        "shared_yammer",
        "shared_yammerenterprise",
        "shared_skypeforbusiness",
        "shared_skype",

        // Power Platform built-ins
        "shared_powerapps",
        "shared_powerappsnotification",
        "shared_powerappsnotificationv2",
        "shared_powerautomateforms",
        "shared_approvals",
        "shared_flowmanagement",
        "shared_flowpush",
        "shared_powerbi",                   // some advanced ops are premium; basic dataset refresh is std
        "shared_powerbinotification",

        // Web / utility
        "shared_rss",
        "shared_bingsearch",
        "shared_youtube",
        "shared_twitter",
        "shared_facebook",                  // deprecated; still legacy std
        "shared_dropbox",
        "shared_googlecalendar",
        "shared_googlecontacts",
        "shared_googletasks",
        "shared_smartsheet",
        "shared_trello",
        "shared_wunderlist",
        "shared_evernote",
        "shared_pinterest",

        // Connectivity helpers (no premium gate at the standard tier)
        "shared_ftp",
        "shared_smtp",
        "shared_pop3",
        "shared_imap",
    };

    /// <summary>
    /// Classify a connector id as standard. Strips an optional <c>shared_</c>
    /// prefix, lower-cases, and looks it up in the curated set. Returns
    /// <c>true</c> only when the connector is positively known to be standard;
    /// unknown / custom / certified-third-party / MS-premium → <c>false</c>.
    /// </summary>
    public static bool IsStandardConnector(string? connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId)) return false;
        var key = connectorId.Trim();
        // Some payloads carry the full ARM path "/providers/Microsoft.PowerApps/apis/shared_x".
        var slash = key.LastIndexOf('/');
        if (slash >= 0 && slash < key.Length - 1) key = key[(slash + 1)..];
        return StandardConnectors.Contains(key);
    }

    /// <summary>
    /// Parse a canvas-app <c>connectionreferences</c> JSON blob and return the
    /// distinct list of connector ids it references. The blob shape is a
    /// dictionary keyed by the per-app reference id; each value carries
    /// <c>connectorName</c> (e.g. "shared_office365") and a full <c>id</c>
    /// like <c>"/providers/Microsoft.PowerApps/apis/shared_office365"</c>.
    /// We return the bare connector id (<c>shared_*</c>) so callers can match
    /// against <see cref="StandardConnectors"/> and against DLP policy
    /// connector ids without worrying about path prefixes.
    /// Returns an empty list on null / empty / malformed JSON — never throws.
    /// </summary>
    public static IReadOnlyList<string> ParseConnectorIds(string? connectionReferencesJson)
    {
        if (string.IsNullOrWhiteSpace(connectionReferencesJson))
            return Array.Empty<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(connectionReferencesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                // Prefer connectorName (clean shared_* form). Fall back to "id"
                // and strip the ARM prefix.
                if (prop.Value.TryGetProperty("connectorName", out var cn) &&
                    cn.ValueKind == JsonValueKind.String &&
                    cn.GetString() is { Length: > 0 } cnVal)
                {
                    ids.Add(cnVal);
                    continue;
                }
                if (prop.Value.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.String &&
                    idEl.GetString() is { Length: > 0 } idVal)
                {
                    var slash = idVal.LastIndexOf('/');
                    if (slash >= 0 && slash < idVal.Length - 1) idVal = idVal[(slash + 1)..];
                    ids.Add(idVal);
                }
            }
        }
        catch
        {
            // best-effort: malformed payload → no connectors. Caller will keep
            // IsPremium=null and DlpStatus="—".
        }
        return ids.Count == 0 ? Array.Empty<string>() : ids.ToArray();
    }

    /// <summary>
    /// Evaluate a single canvas-app's connector list against the tenant DLP
    /// policy snapshot. Returns:
    ///   "Compliant" — every in-scope policy accepts this connector mix
    ///   "Violation" — at least one in-scope policy classifies any connector
    ///                 as Blocked, OR splits two of the asset's connectors
    ///                 across Business and Non-Business
    ///   "—"         — no DLP policies, or no connectors to evaluate
    /// </summary>
    public static string EvaluateDlp(
        string envId,
        IReadOnlyList<string> assetConnectors,
        IReadOnlyList<BapDlpClient.DlpPolicyDto>? policies)
    {
        if (policies is null || policies.Count == 0) return "—";
        if (assetConnectors.Count == 0) return "—";

        foreach (var p in policies)
        {
            if (!IsPolicyInScope(envId, p)) continue;

            // Build a quick connector → classification lookup for this policy.
            // Classifications: "Confidential" (Business), "General" (Non-Business),
            // "Blocked". Everything else falls into the policy's
            // defaultClassification (or "General" if absent).
            var classByConnector = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (p.ConnectorGroups is not null)
            {
                foreach (var grp in p.ConnectorGroups)
                {
                    if (grp.Connectors is null) continue;
                    foreach (var c in grp.Connectors)
                    {
                        // Each policy connector "id" is the full ARM path —
                        // strip to bare connector id for matching.
                        var key = c.Id;
                        if (string.IsNullOrEmpty(key)) continue;
                        var slash = key.LastIndexOf('/');
                        if (slash >= 0 && slash < key.Length - 1) key = key[(slash + 1)..];
                        classByConnector[key] = grp.Classification ?? "General";
                    }
                }
            }
            var defaultClass = p.DefaultClassification ?? "General";

            bool sawBusiness = false, sawNonBusiness = false;
            foreach (var connectorId in assetConnectors)
            {
                var cls = classByConnector.TryGetValue(connectorId, out var v) ? v : defaultClass;
                if (string.Equals(cls, "Blocked", StringComparison.OrdinalIgnoreCase))
                    return "Violation";
                if (string.Equals(cls, "Confidential", StringComparison.OrdinalIgnoreCase))
                    sawBusiness = true;
                else
                    sawNonBusiness = true;
            }
            if (sawBusiness && sawNonBusiness) return "Violation";
        }
        return "Compliant";
    }

    /// <summary>
    /// True when the supplied env is in scope for the given policy:
    ///   AllEnvironments    — always true
    ///   OnlyEnvironments   — true iff envId appears in the policy's environments list
    ///   ExceptEnvironments — true iff envId does NOT appear in the policy's environments list
    /// </summary>
    private static bool IsPolicyInScope(string envId, BapDlpClient.DlpPolicyDto policy)
    {
        var kind = policy.EnvironmentType ?? "AllEnvironments";
        if (string.Equals(kind, "AllEnvironments", StringComparison.OrdinalIgnoreCase))
            return true;
        var listed = policy.Environments?.Any(e =>
            string.Equals(e.Name, envId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Id,   envId, StringComparison.OrdinalIgnoreCase)) ?? false;
        return string.Equals(kind, "OnlyEnvironments", StringComparison.OrdinalIgnoreCase)
            ? listed
            : !listed;
    }
}
