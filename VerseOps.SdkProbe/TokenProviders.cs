using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;

namespace VerseOps.SdkProbe;

/// <summary>App-only (client credentials) â€” single-audience.</summary>
public sealed class AppOnlyTokenProvider : IAccessTokenProvider
{
    private readonly IConfidentialClientApplication _cca;
    private readonly string _scope;
    public AppOnlyTokenProvider(string tenantId, string clientId, string clientSecret, string scope)
    {
        _scope = scope;
        _cca = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();
    }
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();
    public async Task<string> GetAuthorizationTokenAsync(Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        var r = await _cca.AcquireTokenForClient(new[] { _scope }).ExecuteAsync(cancellationToken);
        return r.AccessToken;
    }
}

/// <summary>Static bearer token (paste a token from anywhere).</summary>
public sealed class StaticBearerTokenProvider : IAccessTokenProvider
{
    private readonly string _token;
    public StaticBearerTokenProvider(string token) { _token = token; }
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();
    public Task<string> GetAuthorizationTokenAsync(Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_token);
}
