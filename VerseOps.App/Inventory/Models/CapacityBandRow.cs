namespace VerseOps.App.Inventory.Models;

/// <summary>
/// Display-only projection of one row in the tenant capacity banner
/// (Database / File / Log / API). Pre-computes the consumed/max display
/// string, percent fill (0-100), and color tier so the XAML binding
/// stays declarative — no converters needed in the banner template.
/// </summary>
public sealed class CapacityBandRow
{
    public required string Label { get; init; }

    /// <summary>"123.4 / 1,024 GB" or "12,345 / 50,000 calls/day".</summary>
    public required string UsedOfMaxDisplay { get; init; }

    /// <summary>0-100. Set to 0 when consumed / max are missing.</summary>
    public required double PercentUsed { get; init; }

    /// <summary>"ok" / "warn" / "over". Drives the bar color in XAML.</summary>
    public required string Tier { get; init; }

    public required string TooltipText { get; init; }
}
