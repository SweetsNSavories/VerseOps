using System.IO;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using VerseOps.Api.Core;
using VerseOps.App.Configuration;

namespace VerseOps.App.Auth;

/// <summary>
/// Token acquisition for the WPF app. Supports two modes:
///   - User (delegated): MSAL interactive sign-in via WAM broker on Windows
///                        (falls back to system browser if broker unavailable).
///                        Required for license-gated SKUs (e.g. Developer environments)
///                        and for any operation that must run as the signed-in user.
///   - App-only:         MSAL client credentials using an Azure AD app registration secret.
///                        Identical to VerseOps.Authentication.AppOnlyTokenProvider.
/// </summary>
public sealed class AuthService : IAccessTokenProvider
{
    public enum AuthMode { User, AppOnly }

    private IPublicClientApplication? _publicApp;
    private IConfidentialClientApplication? _confidentialApp;

    public AuthMode Mode { get; set; } = AuthMode.User;

    // User-mode config — defaults sourced from AppSettings so customer-supplied
    // tenant id / client id in appsettings.json flow through automatically.
    public string TenantId { get; set; } = AppSettings.Current.TenantId;
    public string PublicClientId { get; set; } = AppSettings.Current.PublicClientId;

    /// <summary>
    /// When true, use the WAM broker on Windows (silent SSO via the OS account
    /// store). Disabled by default because:
    ///   1) WAM aggressively prefers the signed-in Windows account, which is
    ///      not what we want for an admin tool that may need to sign in as a
    ///      different identity (e.g. tenant admin).
    ///   2) WAM requires a parent window handle for *every* call (even silent),
    ///      which adds fragile plumbing for very little benefit.
    /// With the broker off, we use the system browser interactive flow — the
    /// real https://login.microsoftonline.com page opens in the user's default
    /// browser. Token cache still works across sessions for silent renewal.
    /// </summary>
    public bool UseBroker { get; set; } = false;

    /// <summary>
    /// Provides the parent window handle used by the WAM broker. Only relevant
    /// when <see cref="UseBroker"/> is true. If null, the broker falls back to
    /// the foreground window.
    /// </summary>
    public Func<IntPtr>? WindowHandleProvider { get; set; }

    // App-only config — id defaults from AppSettings; secret is NEVER persisted
    // and only lives in memory for the lifetime of the process.
    public string AppOnlyClientId { get; set; } = AppSettings.Current.AppOnlyClientId;
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
        EnsurePublicClient();

        // IMPORTANT: do NOT fall back to PublicClientApplication.OperatingSystemAccount here.
        // That sentinel tells WAM to silently use the signed-in Windows account, which
        // bypasses any sign-in UI entirely — the user never sees a prompt and we never
        // know which account was used. Only attempt silent acquisition against accounts
        // MSAL has actually cached for this app; otherwise force interactive.
        var accounts = await _publicApp!.GetAccountsAsync().ConfigureAwait(false);
        var account  = accounts.FirstOrDefault();

        AuthenticationResult result;
        if (account is not null)
        {
            try
            {
                result = await _publicApp.AcquireTokenSilent(new[] { scope }, account)
                    .ExecuteAsync(ct).ConfigureAwait(false);
                LastSignedInUser = result.Account?.Username;
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // expected — silent failed because the user must consent / sign in
            }
            catch (MsalClientException ex) when (
                ex.ErrorCode == "wam_failed_to_get_window_handle"
                || ex.Message.Contains("window handle", StringComparison.OrdinalIgnoreCase))
            {
                // WAM tried to bring up a hidden interactive prompt during the
                // silent path and discovered it had no parent window. Fall through
                // to the explicit interactive call below — it does pass the handle.
            }
        }

        result = await AcquireInteractiveAsync(scope, Prompt.SelectAccount, ct).ConfigureAwait(false);
        LastSignedInUser = result.Account?.Username;
        return result.AccessToken;
    }

    /// <summary>
    /// Force an explicit sign-in UI (account picker), regardless of any cached token.
    /// Use this for an explicit "Sign in" button in the UI.
    /// </summary>
    public async Task<string> SignInInteractiveAsync(string scope, CancellationToken ct = default)
    {
        Mode = AuthMode.User;
        LastTokenAudience = scope;

        EnsurePublicClient();

        // Wipe any cached accounts so MSAL can't silent-resolve to the previous user.
        foreach (var a in await _publicApp!.GetAccountsAsync().ConfigureAwait(false))
            await _publicApp.RemoveAsync(a).ConfigureAwait(false);

        var result = await AcquireInteractiveAsync(scope, Prompt.SelectAccount, ct).ConfigureAwait(false);
        LastSignedInUser = result.Account?.Username;
        return result.AccessToken;
    }

    /// <summary>
    /// Device-code sign-in for headless / no-browser hosts (test rigs, SSH sessions,
    /// kiosk machines). MSAL invokes <paramref name="onMessage"/> with the user code
    /// + verification URL; the caller is responsible for surfacing it (write to a
    /// file, print to stderr, pop a notification, etc.). MSAL then polls AAD until
    /// the user finishes the flow on another device or the timeout elapses.
    /// </summary>
    public async Task<string> SignInDeviceCodeAsync(
        string scope,
        Func<DeviceCodeResult, Task> onMessage,
        CancellationToken ct = default)
    {
        Mode = AuthMode.User;
        LastTokenAudience = scope;
        EnsurePublicClient();

        foreach (var a in await _publicApp!.GetAccountsAsync().ConfigureAwait(false))
            await _publicApp.RemoveAsync(a).ConfigureAwait(false);

        var result = await _publicApp.AcquireTokenWithDeviceCode(new[] { scope }, onMessage)
            .ExecuteAsync(ct).ConfigureAwait(false);
        LastSignedInUser = result.Account?.Username;
        return result.AccessToken;
    }

    /// <summary>
    /// Silent-only token acquisition. Returns null when no cached account exists or
    /// the cached refresh token can no longer be used silently. Never opens a browser.
    /// Headless callers (test rigs, sweepers, CI) use this to honor a cached sign-in
    /// without blocking on a UI prompt.
    /// </summary>
    public async Task<string?> TryGetTokenSilentAsync(string scope, CancellationToken ct = default)
    {
        Mode = AuthMode.User;
        LastTokenAudience = scope;
        EnsurePublicClient();
        var accounts = await _publicApp!.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account is null) return null;
        try
        {
            var result = await _publicApp.AcquireTokenSilent(new[] { scope }, account)
                .ExecuteAsync(ct).ConfigureAwait(false);
            LastSignedInUser = result.Account?.Username;
            return result.AccessToken;
        }
        catch (MsalUiRequiredException) { return null; }
        catch (MsalClientException) { return null; }
    }

    private void EnsurePublicClient()
    {
        // Rebuild if the client id changed OR the broker mode changed since the last build.
        var needsBuild = _publicApp is null
            || _publicApp.AppConfig.ClientId != PublicClientId
            || _publicApp.AppConfig.IsBrokerEnabled != UseBroker;
        if (!needsBuild) return;

        var builder = PublicClientApplicationBuilder
            .Create(PublicClientId)
            .WithAuthority($"https://login.microsoftonline.com/{TenantId}")
            .WithDefaultRedirectUri();

        if (UseBroker)
        {
            var brokerOptions = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "VerseOps"
            };
            builder = builder.WithBroker(brokerOptions);
        }

        _publicApp = builder.Build();
        RegisterPersistentCache(_publicApp);
    }

    // Bind MSAL's serializable token cache to a file under %LOCALAPPDATA%\VerseOps,
    // encrypted at rest with DPAPI (Windows current-user scope). Failure to bind
    // is non-fatal — tokens still flow, they just don't survive a process restart.
    private static volatile MsalCacheHelper? s_cacheHelper;
    private static readonly object s_cacheHelperLock = new();
    private static void RegisterPersistentCache(IPublicClientApplication app)
    {
        try
        {
            if (s_cacheHelper == null)
            {
                lock (s_cacheHelperLock)
                {
                    if (s_cacheHelper == null)
                    {
                        var dir = Path.GetDirectoryName(AppSettings.UserSettingsPath)
                                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VerseOps");
                        Directory.CreateDirectory(dir);
                        var props = new StorageCreationPropertiesBuilder("msal_cache.bin", dir).Build();
                        s_cacheHelper = MsalCacheHelper.CreateAsync(props).GetAwaiter().GetResult();
                    }
                }
            }
            s_cacheHelper!.RegisterCache(app.UserTokenCache);
        }
        catch
        {
            // Persistence is best-effort; in-memory cache still works.
        }
    }

    private async Task<AuthenticationResult> AcquireInteractiveAsync(string scope, Prompt prompt, CancellationToken ct)
    {
        var interactive = _publicApp!.AcquireTokenInteractive(new[] { scope })
            .WithPrompt(prompt)
            // Use the system browser, never the embedded WebView. With the broker
            // disabled this means the real Microsoft sign-in page opens in the
            // user's default browser — they see the email/password form (or pick
            // an account that's already signed into the browser).
            .WithUseEmbeddedWebView(false);

        if (UseBroker)
        {
            // WAM REQUIRES a parent window handle. If the host didn't wire one
            // (or it returns IntPtr.Zero before the window is shown), fall back
            // to the OS foreground window so MSAL never throws
            // "A window handle must be configured."
            interactive = interactive.WithParentActivityOrWindow(() =>
            {
                var h = WindowHandleProvider?.Invoke() ?? IntPtr.Zero;
                if (h == IntPtr.Zero) h = NativeMethods.GetForegroundWindow();
                if (h == IntPtr.Zero) h = NativeMethods.GetDesktopWindow();
                return h;
            });
        }

        return await interactive.ExecuteAsync(ct).ConfigureAwait(false);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();
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
