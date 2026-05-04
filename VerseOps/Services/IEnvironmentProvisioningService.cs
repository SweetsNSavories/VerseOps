using VerseOps.Models;

namespace VerseOps.Services;

/// <summary>
/// Control-plane operations for provisioning Power Platform environments.
/// Designed to be injected into a Windows app (WPF / WinForms / WinUI) via DI.
/// </summary>
public interface IEnvironmentProvisioningService
{
    /// <summary>
    /// Creates a new Power Platform environment, optionally with a Dataverse database,
    /// and waits for the asynchronous provisioning operation to reach a terminal state.
    /// </summary>
    /// <param name="request">Environment provisioning input.</param>
    /// <param name="cancellationToken">Cancellation token for the polling loop.</param>
    Task<EnvironmentProvisioningResult> CreateEnvironmentAsync(
        CreateEnvironmentRequest request,
        CancellationToken cancellationToken = default);
}
