namespace VerseOps.Models;

/// <summary>
/// Lifecycle states returned by the BAP "lifecycleOperations" endpoint while
/// an environment is being provisioned asynchronously.
/// </summary>
public enum OperationStatus
{
    NotStarted,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
