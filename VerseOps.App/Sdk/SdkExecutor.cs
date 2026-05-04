using System.Collections;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.PowerPlatform.Management;
using VerseOps.App.Auth;

namespace VerseOps.App.Sdk;

/// <summary>
/// Builds a ServiceClient (Kiota) bound to whichever AuthService mode the user picked,
/// then navigates the path of an SdkOp and invokes its verb method via reflection.
/// </summary>
public sealed class SdkExecutor
{
    private readonly AuthService _auth;
    private const string PpacBaseUrl = "https://api.powerplatform.com";
    private const string PpacScope = "https://api.powerplatform.com/.default";
    private const string ApiVersion = "2022-03-01-preview";

    public SdkExecutor(AuthService auth) { _auth = auth; }

    public async Task<SdkResult> ExecuteAsync(SdkOp op, IReadOnlyDictionary<string, string> indexerValues, string? jsonBody, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var sc = await BuildClientAsync(ct);
            object current = sc;
            // Navigate
            foreach (var step in op.Path)
            {
                var t = current.GetType();
                var prop = t.GetProperty(step.PropertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop is null)
                    return SdkResult.Fail($"Navigation failed: {t.Name}.{step.PropertyName} not found.", sw.ElapsedMilliseconds);
                if (step.IsIndexer)
                {
                    if (!indexerValues.TryGetValue(step.IndexParamName!, out var key) || string.IsNullOrWhiteSpace(key))
                        return SdkResult.Fail($"Missing indexer value for '{step.IndexParamName}'.", sw.ElapsedMilliseconds);
                    current = prop.GetValue(current, new object[] { key })
                              ?? throw new InvalidOperationException($"{step.PropertyName}[{key}] returned null.");
                }
                else
                {
                    current = prop.GetValue(current)
                              ?? throw new InvalidOperationException($"{step.PropertyName} returned null.");
                }
            }

            // Build args
            var ps = op.Method.GetParameters();
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                if (p.ParameterType == typeof(CancellationToken)) { args[i] = ct; continue; }
                if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))
                {
                    args[i] = p.HasDefaultValue ? p.DefaultValue : null;
                    continue;
                }
                if (p == ps.FirstOrDefault(x => x.ParameterType != typeof(CancellationToken)
                    && !(x.ParameterType.IsGenericType && x.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))))
                {
                    // Body parameter — deserialize from JSON when provided.
                    if (!string.IsNullOrWhiteSpace(jsonBody))
                    {
                        try
                        {
                            args[i] = JsonSerializer.Deserialize(jsonBody, p.ParameterType,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }
                        catch (Exception jex)
                        {
                            return SdkResult.Fail($"Could not bind body to {p.ParameterType.Name}: {jex.Message}", sw.ElapsedMilliseconds);
                        }
                    }
                    else if (p.HasDefaultValue) args[i] = p.DefaultValue;
                    else args[i] = null;
                    continue;
                }
                args[i] = p.HasDefaultValue ? p.DefaultValue : null;
            }

            var task = (Task)op.Method.Invoke(current, args)!;
            await task.ConfigureAwait(false);
            object? response = task.GetType().GetProperty("Result")?.GetValue(task);
            sw.Stop();

            return new SdkResult(true, sw.ElapsedMilliseconds, Summarise(response), Render(response), null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            sw.Stop();
            return SdkResult.Fail($"{tie.InnerException.GetType().Name}: {tie.InnerException.Message}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return SdkResult.Fail($"{ex.GetType().Name}: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    private async Task<ServiceClient> BuildClientAsync(CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(PpacScope, ct);
        var provider = new StaticTokenAccessProvider(token);
        var authProv = new BaseBearerTokenAuthenticationProvider(provider);
        var handlers = KiotaClientFactory.CreateDefaultHandlers();
        handlers.Insert(0, new ApiVersionHandler(ApiVersion));
        var http = KiotaClientFactory.Create(handlers);
        var adapter = new HttpClientRequestAdapter(authProv, httpClient: http) { BaseUrl = PpacBaseUrl };
        return new ServiceClient(adapter);
    }

    private static string Summarise(object? response)
    {
        if (response is null) return "(null)";
        var t = response.GetType();
        var v = t.GetProperty("Value")?.GetValue(response);
        if (v is ICollection col) return $"{t.Name}: {col.Count} items";
        if (v is IEnumerable e) { int n = 0; foreach (var _ in e) n++; return $"{t.Name}: {n} items"; }
        return t.Name;
    }

    private static string Render(object? response)
    {
        if (response is null) return "(null)";
        try
        {
            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (Exception ex)
        {
            return $"(could not serialize: {ex.Message})";
        }
    }

    private sealed class StaticTokenAccessProvider : IAccessTokenProvider
    {
        private readonly string _token;
        public StaticTokenAccessProvider(string token) { _token = token; }
        public AllowedHostsValidator AllowedHostsValidator { get; } = new();
        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }

    private sealed class ApiVersionHandler : DelegatingHandler
    {
        private readonly string _ver;
        public ApiVersionHandler(string ver) { _ver = ver; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var u = request.RequestUri;
            if (u != null && (u.Query.Length == 0 || !u.Query.Contains("api-version=", StringComparison.Ordinal)))
            {
                var sep = u.Query.Length == 0 ? "?" : "&";
                request.RequestUri = new Uri($"{u}{sep}api-version={_ver}");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed record SdkResult(bool Success, long ElapsedMs, string StatusText, string Body, string? Error)
{
    public static SdkResult Fail(string msg, long ms) => new(false, ms, "ERROR", msg, msg);
}
