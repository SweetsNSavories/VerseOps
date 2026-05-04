namespace VerseOps.Authentication;

/// <summary>
/// Abstracts token acquisition so the provisioning service can be unit-tested
/// and so a Windows app can later inject a different token source if needed.
/// </summary>
public interface IPowerPlatformTokenProvider
{
    /// <summary>
    /// Acquires an app-only access token for the Power Platform control-plane audience.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
