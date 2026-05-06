namespace VerseOps.App.Inventory.Models;

/// <summary>
/// One capacity row for an environment. CapacityType matches PPAC enum strings
/// like "Database", "File", "Log", "FinOpsDatabase", "FinOpsFile".
/// </summary>
public sealed class CapacityEntry
{
    public required string EnvId { get; init; }
    public required string CapacityType { get; init; }
    public double? Actual { get; set; }
    public double? Rated { get; set; }
    public double? Total { get; set; }
    public DateTime LastSyncedUtc { get; set; }
}
