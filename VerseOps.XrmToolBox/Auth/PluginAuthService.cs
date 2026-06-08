using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using VerseOps.Api.Core;

namespace VerseOps.XrmToolBox.Auth
{
    /// <summary>
    /// MSAL token acquisition for the XrmToolBox plugin. Implements the same
    /// <see cref="IAccessTokenProvider"/> contract <see cref="ApiExecutor"/>
    /// consumes, so the plugin and the WPF app can share the operation catalog
    /// without duplicating Auth glue in <c>VerseOps.Api.Core</c>.
    ///
    /// <para><b>Cache sharing with the WPF app.</b> Points at the same
    /// <c>%LOCALAPPDATA%\VerseOps\msal_cache.bin</c> file (DPAPI, current-user
    /// scope) using the same client id <see cref="DefaultPublicClientId"/> as
    /// <c>VerseOps.App.Configuration.AppConstants.AzureCliPublicClientId</c>.
    /// Signing into the WPF app once means this plugin's
    /// <see cref="TryGetTokenSilentAsync"/> succeeds on first load — no second
    /// browser pop, no second device code.</para>
    ///
    /// <para><b>No broker.</b> Unlike <c>VerseOps.App.Auth.AuthService</c>,
    /// this implementation never uses WAM. XrmToolBox's plugin host doesn't
    /// give us a reliable parent-window handle across docked / floating /
    /// MDI-child states; WAM throws
    /// <c>wam_failed_to_get_window_handle</c> in at least one of them. Two
    /// flows are exposed instead:</para>
    /// <list type="bullet">
    ///   <item><see cref="SignInInteractiveAsync"/> — system browser (the user's
    ///     default Edge/Chrome opens login.microsoftonline.com). Works when
    ///     the host has a desktop session.</item>
    ///   <item><see cref="SignInDeviceCodeAsync"/> — device code flow. Works
    ///     anywhere (RDP, kiosk, headless). The plugin UI surfaces the user
    ///     code + verification URL via the supplied callback.</item>
    /// </list>
    /// </summary>
    public sealed class PluginAuthService : IAccessTokenProvider
    {
        /// <summary>
        /// Azure CLI's well-known public-client app registration. Multi-tenant,
        /// trusted by every Entra tenant out of the box, so first-run users can
        /// sign in without registering their own app. MUST match
        /// <c>VerseOps.App.Configuration.AppConstants.AzureCliPublicClientId</c>
        /// — the MSAL cache is keyed on (clientId, tenantId), so changing it
        /// silently breaks the shared-cache handshake with the WPF app.
        /// </summary>
        public const string DefaultPublicClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

        /// <summary>
        /// "common" lets MSAL accept any tenant. Same default as the WPF app.
        /// Overridable via <see cref="TenantId"/> for single-tenant deployments.
        /// </summary>
        public const string DefaultTenantId = "common";

        // Standard PPAC scope. Plugin callers reference this rather than
        // re-stringing the literal; matches ApiCatalog.ScopePpac.
        public const string ScopePpac = "https://api.powerplatform.com/.default";

        // File name MUST match what VerseOps.App writes via MsalCacheHelper —
        // changing it forks the cache and breaks the silent-SSO handshake.
        private const string CacheFileName = "msal_cache.bin";
        private const string CacheDirName  = "VerseOps";

        private IPublicClientApplication? _publicApp;

        public string TenantId       { get; set; } = DefaultTenantId;
        public string PublicClientId { get; set; } = DefaultPublicClientId;

        /// <summary>Username from the most recent successful token acquisition. Null until signed in.</summary>
        public string? LastSignedInUser { get; private set; }

        /// <summary>
        /// <see cref="IAccessTokenProvider"/> entry point. Tries silent first
        /// (shared cache → no prompt) and falls back to system-browser
        /// interactive. Callers that want device-code instead should invoke
        /// <see cref="SignInDeviceCodeAsync"/> explicitly.
        /// </summary>
        public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
        {
            EnsurePublicClient();

            var accounts = await _publicApp!.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault();
            if (account != null)
            {
                try
                {
                    var silent = await _publicApp
                        .AcquireTokenSilent(new[] { scope }, account)
                        .ExecuteAsync(ct).ConfigureAwait(false);
                    LastSignedInUser = silent.Account?.Username;
                    return silent.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // Cached refresh token can no longer redeem silently
                    // (consent revoked, conditional access change, scope
                    // first-time). Fall through to interactive.
                }
            }

            var result = await _publicApp
                .AcquireTokenInteractive(new[] { scope })
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(ct).ConfigureAwait(false);
            LastSignedInUser = result.Account?.Username;
            return result.AccessToken;
        }

        /// <summary>
        /// Silent-only acquisition. Returns null when no cached account exists
        /// or the cached refresh token can no longer redeem silently. Never
        /// opens a browser. The plugin calls this on load to detect an
        /// existing WPF-app sign-in.
        /// </summary>
        public async Task<string?> TryGetTokenSilentAsync(string scope, CancellationToken ct = default)
        {
            EnsurePublicClient();
            var accounts = await _publicApp!.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault();
            if (account == null) return null;
            try
            {
                var result = await _publicApp
                    .AcquireTokenSilent(new[] { scope }, account)
                    .ExecuteAsync(ct).ConfigureAwait(false);
                LastSignedInUser = result.Account?.Username;
                return result.AccessToken;
            }
            catch (MsalUiRequiredException) { return null; }
            catch (MsalClientException)    { return null; }
        }

        /// <summary>
        /// Force a system-browser interactive sign-in (account picker), bypassing any cached token.
        /// </summary>
        public async Task<string> SignInInteractiveAsync(string scope, CancellationToken ct = default)
        {
            EnsurePublicClient();

            // Drop any cached accounts so MSAL can't silent-resolve to the
            // previous identity before the picker shows.
            foreach (var a in await _publicApp!.GetAccountsAsync().ConfigureAwait(false))
                await _publicApp.RemoveAsync(a).ConfigureAwait(false);

            var result = await _publicApp
                .AcquireTokenInteractive(new[] { scope })
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(ct).ConfigureAwait(false);
            LastSignedInUser = result.Account?.Username;
            return result.AccessToken;
        }

        /// <summary>
        /// Device-code sign-in for hosts without a usable browser (RDP,
        /// headless, kiosk). <paramref name="onMessage"/> receives the user
        /// code + verification URL; the caller is responsible for surfacing
        /// it to the user (label, message box, file, etc.).
        /// </summary>
        public async Task<string> SignInDeviceCodeAsync(
            string scope,
            Func<DeviceCodeResult, Task> onMessage,
            CancellationToken ct = default)
        {
            EnsurePublicClient();

            foreach (var a in await _publicApp!.GetAccountsAsync().ConfigureAwait(false))
                await _publicApp.RemoveAsync(a).ConfigureAwait(false);

            var result = await _publicApp
                .AcquireTokenWithDeviceCode(new[] { scope }, onMessage)
                .ExecuteAsync(ct).ConfigureAwait(false);
            LastSignedInUser = result.Account?.Username;
            return result.AccessToken;
        }

        /// <summary>Wipe all cached accounts and reset signed-in state. Affects the WPF app too (shared cache).</summary>
        public async Task SignOutAsync()
        {
            if (_publicApp == null) return;
            foreach (var a in await _publicApp!.GetAccountsAsync().ConfigureAwait(false))
                await _publicApp.RemoveAsync(a).ConfigureAwait(false);
            LastSignedInUser = null;
        }

        private void EnsurePublicClient()
        {
            var needsBuild = _publicApp == null
                || _publicApp.AppConfig.ClientId != PublicClientId;
            if (!needsBuild) return;

            _publicApp = PublicClientApplicationBuilder
                .Create(PublicClientId)
                .WithAuthority($"https://login.microsoftonline.com/{TenantId}")
                .WithDefaultRedirectUri()
                .Build();
            RegisterPersistentCache(_publicApp);
        }

        // Bind MSAL's serializable token cache to %LOCALAPPDATA%\VerseOps\msal_cache.bin,
        // DPAPI-encrypted (current-user scope). Same path the WPF app uses, so
        // both processes share the cache. Persistence is best-effort: a failure
        // (e.g. roaming-profile quirks, AV interference) leaves the in-memory
        // cache working, the user just re-signs-in next launch.
        private static volatile MsalCacheHelper? s_cacheHelper;
        private static readonly object s_cacheHelperLock = new object();
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
                            var dir = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                CacheDirName);
                            Directory.CreateDirectory(dir);
                            var props = new StorageCreationPropertiesBuilder(CacheFileName, dir).Build();
                            s_cacheHelper = MsalCacheHelper.CreateAsync(props).GetAwaiter().GetResult();
                        }
                    }
                }
                s_cacheHelper!.RegisterCache(app.UserTokenCache);
            }
            catch
            {
                // Non-fatal — in-memory cache still works for this session.
            }
        }
    }
}
