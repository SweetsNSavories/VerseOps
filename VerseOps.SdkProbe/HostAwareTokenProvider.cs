using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Kiota.Abstractions.Authentication;

namespace VerseOps.SdkProbe;

/// <summary>
/// One MSAL public-client login, many audiences. Every call resolves the right scope from the host
/// and uses AcquireTokenSilent against the cached account so no extra device-code prompts are needed.
/// First time a host needs a scope the user hasn't consented to, we transparently fall back to
/// AcquireTokenInteractive (or device-code if interactive is blocked) â€” that's a one-shot consent.
/// </summary>
public sealed class HostAwareTokenProvider : IAccessTokenProvider
{
    public IPublicClientApplication PublicApp { get; }
    private bool _cacheAttached;
    private readonly bool _useInteractiveOnConsent;

    /// <summary>Map host -> "{audience}/.default". Add hosts here to teach the probe new APIs.</summary>
    public static readonly IReadOnlyDictionary<string, string> HostScopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["api.powerplatform.com"]      = "https://api.powerplatform.com/.default",
        ["api.bap.microsoft.com"]      = "https://service.powerapps.com/.default",
        ["api.powerapps.com"]          = "https://service.powerapps.com/.default",
        ["service.powerapps.com"]      = "https://service.powerapps.com/.default",
        ["api.flow.microsoft.com"]     = "https://service.flow.microsoft.com/.default",
        ["service.flow.microsoft.com"] = "https://service.flow.microsoft.com/.default",
        ["graph.microsoft.com"]        = "https://graph.microsoft.com/.default",
    };

    public HostAwareTokenProvider(string tenantId, string publicClientId, bool useInteractiveOnConsent = false)
    {
        _useInteractiveOnConsent = useInteractiveOnConsent;
        PublicApp = PublicClientApplicationBuilder.Create(publicClientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithRedirectUri("http://localhost")
            .Build();
    }

    private async Task EnsureCacheAttachedAsync()
    {
        if (_cacheAttached) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VerseOps.SdkProbe");
            Directory.CreateDirectory(dir);
            var props = new StorageCreationPropertiesBuilder("msal.cache", dir).Build();
            var helper = await MsalCacheHelper.CreateAsync(props);
            helper.RegisterCache(PublicApp.UserTokenCache);
        }
        catch { /* best-effort */ }
        _cacheAttached = true;
    }

    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public async Task<string> GetAuthorizationTokenAsync(Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureCacheAttachedAsync();
        var scope = ResolveScopeForHost(uri.Host);

        var accounts = await PublicApp.GetAccountsAsync();
        var account = accounts.FirstOrDefault();

        if (account != null)
        {
            try
            {
                var s = await PublicApp.AcquireTokenSilent(new[] { scope }, account).ExecuteAsync(cancellationToken);
                return s.AccessToken;
            }
            catch (MsalUiRequiredException) { /* need consent or sign-in for this scope */ }
        }

        // First-time interactive (consent) path. Prefer device-code so headless probes still work.
        if (_useInteractiveOnConsent && OperatingSystem.IsWindows())
        {
            var ir = await PublicApp.AcquireTokenInteractive(new[] { scope })
                .WithAccount(account)
                .ExecuteAsync(cancellationToken);
            return ir.AccessToken;
        }

        var dr = await PublicApp.AcquireTokenWithDeviceCode(new[] { scope }, dc =>
        {
            Console.WriteLine();
            Console.WriteLine($"[consent needed for {scope}] {dc.Message}");
            Console.WriteLine();
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken);
        return dr.AccessToken;
    }

    public static string ResolveScopeForHost(string host)
    {
        if (HostScopes.TryGetValue(host, out var s)) return s;
        // Default to PPAC; better than throwing.
        return "https://api.powerplatform.com/.default";
    }

    /// <summary>Get the cached user's AAD object id (no Graph call needed).</summary>
    public async Task<(string? userId, string? upn, string? tenantId)> GetUserIdentityAsync(CancellationToken ct = default)
    {
        await EnsureCacheAttachedAsync();
        var accounts = await PublicApp.GetAccountsAsync();
        var account = accounts.FirstOrDefault();
        if (account == null) return (null, null, null);
        var (oid, tid) = SplitHomeAccountId(account.HomeAccountId?.Identifier);
        return (oid, account.Username, tid);
    }

    private static (string? oid, string? tid) SplitHomeAccountId(string? home)
    {
        if (string.IsNullOrWhiteSpace(home)) return (null, null);
        var parts = home.Split('.');
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }
}
