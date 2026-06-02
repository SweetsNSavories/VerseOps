using System.Collections;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
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
    // Default api-version baked into the Kiota SDK request templates. The handler
    // overrides this per-path where PPAC has moved an endpoint to a newer api-version
    // (see ApiVersionHandler.ResolveVersionForPath).
    private const string ApiVersion = "2022-03-01-preview";

    public SdkExecutor(AuthService auth) { _auth = auth; }

    public async Task<SdkResult> ExecuteAsync(SdkOp op, IReadOnlyDictionary<string, string> indexerValues, string? jsonBody, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ErrorBodyCaptureHandler capture;
        ServiceClient sc;
        try
        {
            (sc, capture) = await BuildClientAsync(ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return SdkResult.Fail($"{ex.GetType().Name}: {ex.Message}", sw.ElapsedMilliseconds);
        }
        try
        {
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
                    if (!indexerValues.TryGetValue(step.SlotKey, out var key) || string.IsNullOrWhiteSpace(key))
                        return SdkResult.Fail($"Missing indexer value for '{step.FriendlyParamName}' (slot '{step.SlotKey}').", sw.ElapsedMilliseconds);
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
                    // Body parameter ΓÇö deserialize from JSON when provided.
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

            return new SdkResult(true, sw.ElapsedMilliseconds, Summarise(response), Render(response), null)
            {
                HttpStatusCode    = capture.LastStatusCode,
                OperationLocation = capture.LastOperationLocation,
                CorrelationId     = capture.LastCorrelationId,
            };
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            sw.Stop();
            return BuildExceptionResult(tie.InnerException, sw.ElapsedMilliseconds, capture);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return BuildExceptionResult(ex, sw.ElapsedMilliseconds, capture);
        }
    }

    /// <summary>
    /// Kiota's ApiException carries the HTTP status code and (often) the raw response
    /// headers + body. Surface them so the user sees the same level of detail as REST
    /// mode ΓÇö not just "ApiException: 400".
    /// </summary>
    private static SdkResult BuildExceptionResult(Exception ex, long elapsedMs, ErrorBodyCaptureHandler? capture = null)
    {
        // ApiException is in Microsoft.Kiota.Abstractions and exposes ResponseStatusCode + ResponseHeaders.
        if (ex is ApiException apiEx)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"HTTP {apiEx.ResponseStatusCode}   {ex.GetType().Name}");
            sb.AppendLine($"Message: {apiEx.Message}");
            // PPAC EnvironmentManagement.Settings returns 403 when the signed-in identity is missing
            // the EnvironmentManagement.Settings.Read delegated scope. Surface a one-line hint so the
            // user knows what to fix rather than just "HTTP 403".
            if (apiEx.ResponseStatusCode == 403)
            {
                sb.AppendLine();
                sb.AppendLine("Hint: 403 from PPAC usually means the signed-in identity is missing a delegated scope");
                sb.AppendLine("      (e.g. EnvironmentManagement.Settings.Read). For user auth, re-consent in the auth");
                sb.AppendLine("      panel. For app-only, grant Application permissions and admin-consent the SP.");
            }
            var url = capture?.LastRequestUrl;
            if (!string.IsNullOrEmpty(url))
            {
                sb.AppendLine();
                sb.AppendLine("--- Request ---");
                sb.AppendLine(url);
            }
            if (apiEx.ResponseHeaders != null && apiEx.ResponseHeaders.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- Response headers ---");
                foreach (var h in apiEx.ResponseHeaders)
                    sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
            }
            var contentHeaders = capture?.LastContentHeaders;
            if (!string.IsNullOrEmpty(contentHeaders))
            {
                sb.AppendLine();
                sb.AppendLine("--- Content headers ---");
                sb.AppendLine(contentHeaders);
            }
            // Raw body that our DelegatingHandler captured before Kiota disposed it.
            var raw = capture?.LastBody;
            var bodyLen = capture?.LastBodyLength ?? 0;
            sb.AppendLine();
            sb.AppendLine($"--- Raw response body ({bodyLen} bytes) ---");
            if (!string.IsNullOrEmpty(raw))
                sb.AppendLine(PrettyJsonOrRaw(raw));
            else
                sb.AppendLine("(empty)");
            // Some Kiota error types also add typed properties via reflection.
            var extra = SerializeExceptionPayload(ex);
            if (!string.IsNullOrEmpty(extra))
            {
                sb.AppendLine();
                sb.AppendLine("--- Exception typed properties ---");
                sb.AppendLine(extra);
            }
            return new SdkResult(false, elapsedMs, $"HTTP {apiEx.ResponseStatusCode}", sb.ToString(), apiEx.Message)
            {
                HttpStatusCode    = apiEx.ResponseStatusCode,
                OperationLocation = capture?.LastOperationLocation,
                CorrelationId     = capture?.LastCorrelationId,
            };
        }
        return SdkResult.Fail($"{ex.GetType().Name}: {ex.Message}", elapsedMs) with
        {
            HttpStatusCode    = capture?.LastStatusCode,
            OperationLocation = capture?.LastOperationLocation,
            CorrelationId     = capture?.LastCorrelationId,
        };
    }

    private static string PrettyJsonOrRaw(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return s; }
    }

    private static string SerializeExceptionPayload(Exception ex)
    {
        try
        {
            // Skip the standard Exception properties; only emit anything the SDK added.
            var standard = new HashSet<string>(StringComparer.Ordinal)
            {
                "Message", "Data", "InnerException", "TargetSite", "StackTrace", "HelpLink", "Source", "HResult",
                "ResponseStatusCode", "ResponseHeaders"
            };
            var props = ex.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => !standard.Contains(p.Name))
                .ToDictionary(p => p.Name, p => { try { return p.GetValue(ex); } catch { return null; } });
            if (props.Count == 0) return string.Empty;
            return JsonSerializer.Serialize(props, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (Exception ser)
        {
            return $"(could not serialize exception payload: {ser.Message})";
        }
    }

    private async Task<(ServiceClient Client, ErrorBodyCaptureHandler Capture)> BuildClientAsync(CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(PpacScope, ct);
        var provider = new StaticTokenAccessProvider(token);
        var authProv = new BaseBearerTokenAuthenticationProvider(provider);
        var handlers = KiotaClientFactory.CreateDefaultHandlers();
        handlers.Insert(0, new ApiVersionHandler(ApiVersion));
        // Fresh capture handler per call — DelegatingHandler.InnerHandler can only be
        // parented once. Reusing a single instance across requests throws
        // InvalidOperationException("This instance has already started one or more
        // requests") on the second call.
        var capture = new ErrorBodyCaptureHandler();
        handlers.Insert(0, capture); // outermost so it sees the final response
        var http = KiotaClientFactory.Create(handlers);
        var adapter = new HttpClientRequestAdapter(authProv, httpClient: http) { BaseUrl = PpacBaseUrl };
        return (new ServiceClient(adapter), capture);
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
        private readonly string _defaultVer;
        public ApiVersionHandler(string ver) { _defaultVer = ver; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var u = request.RequestUri;
            if (u != null)
            {
                var ub = new UriBuilder(u);
                var q = System.Web.HttpUtility.ParseQueryString(ub.Query);
                var current = q["api-version"];
                var desired = ResolveVersionForPath(u.AbsolutePath);
                // Kiota's request template emits "api-version=" with no value when the
                // generated query-parameter property is unset, so "key present" is not
                // enough — we must also check that the value is non-empty. We also
                // overwrite a value that doesn't match the per-path desired version
                // (PPAC /settings only accepts 2024-10-01; the SDK pins the older one).
                if (string.IsNullOrEmpty(current) ||
                    !string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                {
                    q["api-version"] = desired;
                    ub.Query = q.ToString();
                    request.RequestUri = ub.Uri;
                }
            }
            return base.SendAsync(request, cancellationToken);
        }

        private string ResolveVersionForPath(string absolutePath)
        {
            // PPAC EnvironmentManagement.Settings endpoints return HTTP 403 at
            // api-version=2022-03-01-preview but succeed at 2024-10-01 GA. The SDK
            // NuGet 2.0.3317.207 still emits the older preview — force-upgrade here.
            if (absolutePath != null &&
                absolutePath.IndexOf("/settings", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "2024-10-01";
            }
            return _defaultVer;
        }
    }

    /// <summary>
    /// Captures the raw response body of any non-2xx response into an AsyncLocal so
    /// BuildExceptionResult can include it in the user-visible error ΓÇö Kiota otherwise
    /// disposes the response stream before throwing ApiException.
    /// </summary>
    private sealed class ErrorBodyCaptureHandler : DelegatingHandler
    {
        // Instance fields, not AsyncLocal — AsyncLocal mutations inside SendAsync
        // don't flow back up to the caller's continuation (ExecutionContext is
        // captured on entry, not restored on exit). ExecuteAsync holds a direct
        // reference to this handler, so it reads the fields directly.
        public string? LastBody;
        public int LastBodyLength;
        public string? LastContentHeaders;
        public string? LastRequestUrl;
        public int? LastStatusCode;
        public string? LastOperationLocation;
        public string? LastCorrelationId;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = null;
            LastBodyLength = 0;
            LastContentHeaders = null;
            LastRequestUrl = request.RequestUri is null ? null : $"{request.Method} {request.RequestUri}";
            LastStatusCode = null;
            LastOperationLocation = null;
            LastCorrelationId = null;
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Re-capture using response.RequestMessage so the URL reflects any downstream
            // rewrites (e.g. ApiVersionHandler injecting/overriding api-version). Without
            // this the user sees the pre-rewrite URL with an empty "api-version=" —
            // misleading when diagnosing 403/404 from PPAC.
            var finalReq = response.RequestMessage ?? request;
            if (finalReq.RequestUri is not null)
                LastRequestUrl = $"{finalReq.Method} {finalReq.RequestUri}";
            LastStatusCode = (int)response.StatusCode;
            // Capture op-location / correlation id on EVERY response — 202 long-running
            // operations (e.g. SDK PostAsync for Create Environment) hand back the operation
            // id ONLY via the operation-location header; without this the UI loses it because
            // the SDK's typed return doesn't expose response headers.
            if (response.Headers.TryGetValues("operation-location", out var opLoc))
                LastOperationLocation = opLoc.FirstOrDefault();
            else if (response.Headers.Location is { } loc)
                LastOperationLocation = loc.ToString();
            if (response.Headers.TryGetValues("x-ms-correlation-id", out var corr))
                LastCorrelationId = corr.FirstOrDefault();
            if (!response.IsSuccessStatusCode && response.Content != null)
            {
                try
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    LastBodyLength = bytes.Length;
                    LastBody = Encoding.UTF8.GetString(bytes);
                    if (response.Content.Headers != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var h in response.Content.Headers)
                            sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                        LastContentHeaders = sb.ToString().TrimEnd();
                    }
                    // Re-create the content so downstream Kiota code can still read it.
                    var copy = new ByteArrayContent(bytes);
                    if (response.Content.Headers != null)
                    {
                        foreach (var h in response.Content.Headers)
                            copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    response.Content = copy;
                }
                catch { /* best-effort capture */ }
            }
            return response;
        }
    }
}

public sealed record SdkResult(bool Success, long ElapsedMs, string StatusText, string Body, string? Error)
{
    /// <summary>Numeric HTTP status (when the call reached the wire). Null for pre-flight failures.</summary>
    public int? HttpStatusCode { get; init; }
    /// <summary>operation-location / Location header — populated for 202 long-running operations.</summary>
    public string? OperationLocation { get; init; }
    /// <summary>x-ms-correlation-id — useful when escalating to PPAC support.</summary>
    public string? CorrelationId { get; init; }
    public static SdkResult Fail(string msg, long ms)
    {
        // Trim a short reason into StatusText so the response-meta line is readable;
        // the full message is still available in Body/Error.
        var firstLine = (msg ?? "").Split('\n', 2)[0].Trim();
        if (firstLine.Length > 80) firstLine = firstLine[..80] + "…";
        return new(false, ms, string.IsNullOrEmpty(firstLine) ? "ERROR" : firstLine, msg ?? "", msg);
    }
}
