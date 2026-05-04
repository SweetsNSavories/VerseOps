using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Identity.Client;

namespace VerseOps.SdkRunner;

/// <summary>
/// Kiota IAccessTokenProvider that mints app-only tokens via MSAL client credentials.
/// </summary>
internal sealed class AppOnlyAccessTokenProvider : IAccessTokenProvider
{
    private readonly IConfidentialClientApplication _app;
    private readonly string _scope;

    public AppOnlyAccessTokenProvider(string tenantId, string clientId, string clientSecret, string scope)
    {
        _app = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenantId}"))
            .Build();
        _scope = scope;
    }

    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _app.AcquireTokenForClient(new[] { _scope }).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return result.AccessToken;
    }
}
