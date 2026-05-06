using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// HTTP pipeline tracer for the inventory service. Writes a one-line summary
/// of every request/response (method, url, status, duration) plus the full
/// response body whenever the status is non-2xx, into
/// <c>%LOCALAPPDATA%\VerseOps\inventory-trace.log</c>. Also exposes the last
/// failed request via <see cref="LastFailure"/> so callers can include the
/// captured body in user-visible error messages — Kiota otherwise disposes
/// the response stream before throwing <c>ApiException</c>.
/// </summary>
public sealed class HttpDiagnosticsHandler : DelegatingHandler
{
    private readonly string _logPath;
    private readonly object _writeGate = new();

    public static FailureSnapshot? LastFailure { get; private set; }

    public string LogPath => _logPath;

    /// <summary>
    /// Predicate that decides whether a non-2xx response should get a full
    /// header+body dump in the trace. When it returns true (the default for
    /// everything), the dump is written; when false, only the one-line summary.
    /// Use this to suppress benign 404s like /allocations "no records here".
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage, bool> ShouldDumpFailure { get; set; }
        = static (_, _) => true;

    public HttpDiagnosticsHandler()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VerseOps");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "inventory-trace.log");
    }

    public void ResetLog()
    {
        lock (_writeGate)
        {
            try { File.WriteAllText(_logPath, $"# inventory trace started {DateTime.UtcNow:O}\r\n"); }
            catch { /* best effort */ }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var startUtc = DateTime.UtcNow;
        HttpResponseMessage response;
        Exception? sendError = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sendError = ex;
            sw.Stop();
            Append($"{startUtc:O}  {request.Method} {request.RequestUri}  -> THREW {ex.GetType().Name}: {ex.Message}  ({sw.Elapsed.TotalMilliseconds:0}ms)");
            throw;
        }
        sw.Stop();

        var statusInt = (int)response.StatusCode;
        var line = $"{startUtc:O}  {request.Method,-6} {request.RequestUri}  -> {statusInt} {response.ReasonPhrase}  ({sw.Elapsed.TotalMilliseconds:0}ms)";

        if (response.IsSuccessStatusCode)
        {
            Append(line);
            return response;
        }

        // Non-2xx: capture body so callers can show it.
        string? body = null;
        try
        {
            if (response.Content != null)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                body = Encoding.UTF8.GetString(bytes);
                // Re-create content so Kiota's ApiException factory can still read it.
                var copy = new ByteArrayContent(bytes);
                foreach (var h in response.Content.Headers)
                    copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
                response.Content = copy;
            }
        }
        catch (Exception ex)
        {
            body = $"(could not read body: {ex.GetType().Name}: {ex.Message})";
        }

        bool dump;
        try { dump = ShouldDumpFailure(request, response); }
        catch { dump = true; }

        if (!dump)
        {
            // Quiet path: still record the one-liner, but skip the headers/body block
            // and don't update LastFailure (so the UI doesn't surface it as the
            // most-recent error).
            Append(line);
            return response;
        }

        var sb = new StringBuilder();
        sb.AppendLine(line);
        sb.AppendLine("  Request headers:");
        foreach (var h in request.Headers)
            sb.AppendLine($"    {h.Key}: {Redact(h.Key, string.Join(",", h.Value))}");
        sb.AppendLine("  Response headers:");
        foreach (var h in response.Headers)
            sb.AppendLine($"    {h.Key}: {string.Join(",", h.Value)}");
        if (response.Content?.Headers is { } ch)
            foreach (var h in ch)
                sb.AppendLine($"    {h.Key}: {string.Join(",", h.Value)}");
        sb.AppendLine("  Body:");
        sb.AppendLine(IndentLines(body ?? "(no body)", "    "));
        Append(sb.ToString().TrimEnd());

        LastFailure = new FailureSnapshot(
            Method: request.Method.Method,
            Url: request.RequestUri?.ToString() ?? "(null)",
            Status: statusInt,
            ReasonPhrase: response.ReasonPhrase,
            Body: body,
            CapturedUtc: DateTime.UtcNow);

        return response;
    }

    private void Append(string text)
    {
        lock (_writeGate)
        {
            try { File.AppendAllText(_logPath, text + "\r\n"); }
            catch { /* best effort */ }
        }
    }

    private static string IndentLines(string text, string indent)
    {
        if (string.IsNullOrEmpty(text)) return indent + "(empty)";
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length + lines.Length * indent.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(indent);
            sb.Append(lines[i].TrimEnd('\r'));
            if (i < lines.Length - 1) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Redact(string headerName, string value)
    {
        if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            // Show "Bearer <first 8 chars>... (len=N)" so we can confirm a token was sent
            // without leaking it to the trace file.
            var sp = value.IndexOf(' ');
            var scheme = sp > 0 ? value[..sp] : "Bearer";
            var token = sp > 0 ? value[(sp + 1)..] : value;
            var preview = token.Length > 8 ? token[..8] + "..." : token;
            return $"{scheme} {preview} (len={token.Length})";
        }
        return value;
    }
}

public sealed record FailureSnapshot(
    string Method,
    string Url,
    int Status,
    string? ReasonPhrase,
    string? Body,
    DateTime CapturedUtc);
