namespace VerseOps.App.Inventory.Models;

/// <summary>
/// One pay-as-you-go billing policy row from PPAC
/// <c>Licensing.BillingPolicies</c>. The Azure subscription / resource
/// group / resource id come from the nested <c>billingInstrument</c>
/// object. <see cref="AttachedEnvironmentCount"/> is populated by a
/// follow-up <c>BillingPolicies[id].Environments</c> call.
/// </summary>
public sealed class BillingPolicyRow
{
    public required string PolicyId { get; init; }

    public string? Name { get; set; }
    public string? Location { get; set; }

    /// <summary>"Enabled" / "Disabled" / "Provisioning" / etc.</summary>
    public string? Status { get; set; }

    public string? BillingInstrumentSubscriptionId { get; set; }
    public string? BillingInstrumentResourceGroup { get; set; }
    public string? BillingInstrumentResourceId { get; set; }

    /// <summary>Count of environments attached to this policy.</summary>
    public int AttachedEnvironmentCount { get; set; }

    public DateTime LastSyncedUtc { get; set; }
}
