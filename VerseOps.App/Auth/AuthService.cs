using Microsoft.Identity.Client;

namespace VerseOps.App.Auth;

/// <summary>
/// Token acquisition for the WPF app. Supports two modes:
///   - User (delegated): MSAL interactive sign-in via embedded WebView2 / system browser.
///                        Required for license-gated SKUs (e.g. Developer environments)
///                        and for any operation that must run as the signed-in user.
///   - App-only:         MSAL client credentials using an Azure AD app registration secret.
///                        Identical to VerseOps.Authentication.AppOnlyTokenProvider.
/// </summary>
public sealed class AuthService
{
    public enum AuthMode { User, AppOnly }

    private const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"; // public, well-known
    private IPublicClientApplication? _publicApp;
    private IConfidentialClientApplication? _confidentialApp;

    public AuthMode Mode { get; set; } = AuthMode.User;

    // User-mode config
    public string TenantId { get; set; } = "common";
    public string PublicClientId { get; set; } = AzureCliClientId;

    // App-only config
    public string AppOnlyClientId { get; set; } = string.Empty;
    public string AppOnlyClientSecret { get; set; } = string.Empty;

    public string? LastSignedInUser { get; private set; }
    public string? LastTokenAudience { get; private set; }

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
    {
        LastTokenAudience = scope;
        return Mode switch
        {
            AuthMode.User => await GetUserTokenAsync(scope, ct).ConfigureAwait(false),
            AuthMode.AppOnly => await GetAppOnlyTokenAsync(scope, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown auth mode.")
        };
    }

    private async Task<string> GetUserTokenAsync(string scope, CancellationToken ct)
    {
        if (_publicApp is null || _publicApp.AppConfig.ClientId != PublicClientId)
        {
            _publicApp = PublicClientApplicationBuilder
                .Create(PublicClientId)
                .WithAuthority($"https://login.microsoftonline.com/{TenantId}")
                .WithDefaultRedirectUri()
                .Build();
        }

        var accounts = await _publicApp.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        AuthenticationResult result;
        try
        {
            if (account is null) throw new MsalUiRequiredException("no_account", "No cached account");
            result = await _publicApp.AcquireTokenSilent(new[] { scope }, account)
                .ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            result = await _publicApp.AcquireTokenInteractive(new[] { scope })
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(ct).ConfigureAwait(false);
        }

        LastSignedInUser = result.Account?.Username;
        return result.AccessToken;
    }

    private async Task<string> GetAppOnlyTokenAsync(string scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(AppOnlyClientId) || string.IsNullOrWhiteSpace(AppOnlyClientSecret))
            throw new InvalidOperationException("App-only ClientId and ClientSecret are required.");

        if (_confidentialApp is null || _confidentialApp.AppConfig.ClientId != AppOnlyClientId)
        {
            _confidentialApp = ConfidentialClientApplicationBuilder
                .Create(AppOnlyClientId)
                .WithClientSecret(AppOnlyClientSecret)
                .WithAuthority(new Uri($"https://login.microsoftonline.com/{TenantId}"))
                .Build();
        }

        var result = await _confidentialApp
            .AcquireTokenForClient(new[] { scope })
            .ExecuteAsync(ct).ConfigureAwait(false);

        LastSignedInUser = $"app:{AppOnlyClientId}";
        return result.AccessToken;
    }

    public async Task SignOutAsync()
    {
        if (_publicApp is not null)
        {
            foreach (var a in await _publicApp.GetAccountsAsync().ConfigureAwait(false))
                await _publicApp.RemoveAsync(a).ConfigureAwait(false);
        }
        _confidentialApp = null;
        LastSignedInUser = null;
    }
}
