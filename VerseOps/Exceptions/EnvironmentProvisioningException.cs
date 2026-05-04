namespace VerseOps.Exceptions;

/// <summary>
/// Thrown when environment provisioning fails or the long-running operation does not
/// reach a terminal Succeeded state within the configured timeout.
/// </summary>
public sealed class EnvironmentProvisioningException : Exception
{
    public string? CorrelationId { get; }
    public string? OperationId { get; }
    public string? OperationStatus { get; }

    public EnvironmentProvisioningException(
        string message,
        string? correlationId = null,
        string? operationId = null,
        string? operationStatus = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        CorrelationId = correlationId;
        OperationId = operationId;
        OperationStatus = operationStatus;
    }
}
