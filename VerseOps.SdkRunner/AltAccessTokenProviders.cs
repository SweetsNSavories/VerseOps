using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Identity.Client;

namespace VerseOps.SdkRunner;

/// <summary>Device-code flow — no browser launch needed; prints a code + URL.</summary>
internal sealed class DeviceCodeAccessTokenProvider : IAccessTokenProvider
{
    private const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private readonly IPublicClientApplication _app;
    private readonly string _scope;
    public IPublicClientApplication PublicApp => _app;

    public DeviceCodeAccessTokenProvider(string tenantId, string scope, string? publicClientId = null)
    {
        _app = PublicClientApplicationBuilder
            .Create(publicClientId ?? AzureCliClientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
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
        try
        {
            var account = accounts.FirstOrDefault()
                          ?? throw new MsalUiRequiredException("no_account", "no cached account");
            var silent = await _app.AcquireTokenSilent(new[] { _scope }, account)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException) { /* fall through */ }

        var result = await _app.AcquireTokenWithDeviceCode(new[] { _scope }, dc =>
        {
            Console.WriteLine();
            Console.WriteLine("================ DEVICE CODE SIGN-IN ================");
            Console.WriteLine(dc.Message);
            Console.WriteLine("=====================================================");
            Console.WriteLine();
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return result.AccessToken;
    }
}

/// <summary>Static bearer token (you bring your own — e.g. from `az account get-access-token`).</summary>
internal sealed class StaticBearerTokenProvider : IAccessTokenProvider
{
    private readonly string _token;
    public StaticBearerTokenProvider(string token) => _token = token;
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();
    public Task<string> GetAuthorizationTokenAsync(Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default) => Task.FromResult(_token);
}
