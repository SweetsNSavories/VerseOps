namespace VerseOps.App.Inventory.Models;

/// <summary>
/// One tenant-wide capacity row from PPAC <c>Licensing.TenantCapacity</c>
/// (TenantCapacityAndConsumptionModel). CapacityType matches the SDK enum
/// names: Database / File / Log / TrialDatabase / FinOpsDatabase /
/// ApiCallCount / etc. Storage capacities are reported in MB by the SDK.
/// </summary>
public sealed class TenantCapacityEntry
{
    public required string CapacityType { get; init; }

    /// <summary>Reported units (Unit, MB).</summary>
    public string? Units { get; set; }

    public double? MaxCapacity { get; set; }
    public double? TotalCapacity { get; set; }
    public double? Consumed { get; set; }
    public string? Status { get; set; }
    public DateTime LastSyncedUtc { get; set; }

    /// <summary>True when SDK reports MB units (so divide by 1024 for GB).</summary>
    public bool IsMb => string.Equals(Units, "MB", StringComparison.OrdinalIgnoreCase);
}
