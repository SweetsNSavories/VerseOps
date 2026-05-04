namespace VerseOps.Models;

/// <summary>
/// Result of a successful environment provisioning operation.
/// </summary>
public sealed record EnvironmentProvisioningResult
{
    public required string EnvironmentId { get; init; }
    public required string DisplayName { get; init; }
    public required string Region { get; init; }
    public required EnvironmentType EnvironmentType { get; init; }

    /// <summary>Dataverse organization URL when Dataverse was provisioned; otherwise null.</summary>
    public string? DataverseUrl { get; init; }

    /// <summary>Correlation id of the long-running provisioning operation.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Final lifecycle operation id (used for audit/troubleshooting).</summary>
    public required string OperationId { get; init; }
}
