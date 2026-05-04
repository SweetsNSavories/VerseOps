using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Identity.Client;

namespace VerseOps.SdkRunner;

/// <summary>
/// Kiota IAccessTokenProvider that mints DELEGATED tokens via MSAL public-client
/// interactive sign-in (uses the system browser). Tokens are cached in memory
/// and refreshed silently for the duration of the process.
/// </summary>
internal sealed class InteractiveAccessTokenProvider : IAccessTokenProvider
{
    // Azure CLI's well-known public client id — works in any tenant.
    private const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private readonly IPublicClientApplication _app;
    private readonly string _scope;
    public IPublicClientApplication PublicApp => _app;

    public InteractiveAccessTokenProvider(string tenantId, string scope, string? publicClientId = null)
    {
        _app = PublicClientApplicationBuilder
            .Create(publicClientId ?? AzureCliClientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithDefaultRedirectUri()
            .Build();
        _scope = scope;
    }

    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        AuthenticationResult result;
        try
        {
            var account = accounts.FirstOrDefault()
                          ?? throw new MsalUiRequiredException("no_account", "no cached account");
            result = await _app.AcquireTokenSilent(new[] { _scope }, account)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            result = await _app.AcquireTokenInteractive(new[] { _scope })
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        return result.AccessToken;
    }
}
