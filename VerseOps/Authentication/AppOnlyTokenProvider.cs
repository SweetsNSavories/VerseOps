using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using VerseOps.Configuration;

namespace VerseOps.Authentication;

/// <summary>
/// Acquires app-only (client credentials) tokens for the Power Platform control plane
/// using MSAL. No user interaction, no on-behalf-of, no delegated permissions.
///
/// MSAL caches and refreshes the token internally, so a single instance of this provider
/// can be reused for the lifetime of the host process (e.g. a Windows app).
/// </summary>
public sealed class AppOnlyTokenProvider : IPowerPlatformTokenProvider
{
    private readonly PowerPlatformOptions _options;
    private readonly ILogger<AppOnlyTokenProvider> _logger;
    private readonly IConfidentialClientApplication _app;

    public AppOnlyTokenProvider(
        IOptions<PowerPlatformOptions> options,
        ILogger<AppOnlyTokenProvider> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _app = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{_options.TenantId}"))
            .Build();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _app
                .AcquireTokenForClient(new[] { _options.PowerPlatformScope })
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Acquired Power Platform token. Source={TokenSource} ExpiresOn={ExpiresOn}",
                result.AuthenticationResultMetadata.TokenSource,
                result.ExpiresOn);

            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            _logger.LogError(ex,
                "MSAL failed to acquire app-only token. CorrelationId={CorrelationId} ErrorCode={ErrorCode}",
                ex.CorrelationId, ex.ErrorCode);
            throw;
        }
    }
}
