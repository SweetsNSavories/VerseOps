using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VerseOps.App.Auth;

namespace VerseOps.App.Api;

public sealed record ApiCallResult(
    int StatusCode,
    string ReasonPhrase,
    string ResponseBody,
    string? CorrelationId,
    string? OperationLocation,
    long ElapsedMs,
    IReadOnlyDictionary<string, string> ResponseHeaders
);

public sealed class ApiExecutor
{
    private readonly HttpClient _http = new();
    private readonly AuthService _auth;

    public ApiExecutor(AuthService auth) => _auth = auth;

    public async Task<ApiCallResult> ExecuteAsync(
        string method,
        string url,
        string? body,
        string scope,
        CancellationToken ct = default)
    {
        if (url.StartsWith("local://decode-token", StringComparison.OrdinalIgnoreCase))
        {
            var tok = await _auth.GetTokenAsync(scope, ct).ConfigureAwait(false);
            var decoded = DecodeJwtClaims(tok);
            return new ApiCallResult(200, "OK (local decode)", decoded, null, null, 0,
                new Dictionary<string, string> { ["x-local"] = "jwt-decode" });
        }

        var token = await _auth.GetTokenAsync(scope, ct).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("x-ms-client-request-id", Guid.NewGuid().ToString());
        if (!string.IsNullOrWhiteSpace(body) && method is not "GET" and not "DELETE")
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        sw.Stop();

        var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // Some 4xx responses (notably BAP 403) come back with no body. Synthesize a
        // minimal JSON envelope so the UI has something to render and the user is
        // not left looking at an empty Response panel.
        if (string.IsNullOrWhiteSpace(respBody))
        {
            respBody = $"{{ \"status\": {(int)resp.StatusCode}, \"reason\": \"{resp.ReasonPhrase}\", \"note\": \"Server returned an empty response body.\" }}";
        }
        respBody = TryPrettyPrint(respBody);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);

        headers.TryGetValue("x-ms-correlation-request-id", out var correlation);
        headers.TryGetValue("Location", out var location);
        location ??= headers.GetValueOrDefault("Operation-Location");

        return new ApiCallResult(
            (int)resp.StatusCode, resp.ReasonPhrase ?? "",
            respBody, correlation, location, sw.ElapsedMilliseconds, headers);
    }

    private static string TryPrettyPrint(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        try
        {
            using var doc = JsonDocument.Parse(s);
            return JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return s;
        }
    }

    public static string DecodeJwtClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return "(not a JWT)";
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            var json = Encoding.UTF8.GetString(bytes);
            return TryPrettyPrint(json);
        }
        catch (Exception ex)
        {
            return $"(decode failed: {ex.Message})";
        }
    }
}
