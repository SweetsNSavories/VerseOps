namespace VerseOps.App.Inventory.Models;

/// <summary>
/// One per-currency capacity report row from PPAC
/// <c>Licensing.TenantCapacity.CurrencyReports</c>. The SDK returns one
/// item per currency code (USD / EUR / GBP / ...) describing how many
/// units the tenant has purchased, how many have been allocated to envs,
/// and how many are still free to allocate. Used by the per-currency
/// flyout under the tenant-capacity banner.
/// </summary>
public sealed class TenantCurrencyReportEntry
{
    public required string CurrencyCode { get; init; }

    public double? Purchased { get; set; }
    public double? Allocated { get; set; }
    public double? Consumed  { get; set; }

    /// <summary>Purchased - Allocated, or null if either side is unknown.</summary>
    public double? Available => Purchased.HasValue && Allocated.HasValue
        ? Purchased.Value - Allocated.Value
        : (double?)null;

    public DateTime LastSyncedUtc { get; set; }
}
