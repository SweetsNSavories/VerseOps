using System.Net.Http;

namespace VerseOps.SdkProbe;

/// <summary>
/// Reads the response body once on non-2xx so Kiota's "no error factory registered for code: 4xx"
/// messages can be enriched with the actual server error JSON. Stores the body in a plain static
/// (the probe issues calls serially) so it's visible from any execution context.
/// </summary>
public sealed class ErrorBodyCaptureHandler : DelegatingHandler
{
    private static string? _lastBody;
    public static string? GetAndClear()
    {
        var b = _lastBody;
        _lastBody = null;
        return b;
    }
    public static void Reset() => _lastBody = null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)resp.StatusCode >= 400 && resp.Content != null)
        {
            try
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    _lastBody = System.Text.Encoding.UTF8.GetString(bytes);
                    var ct = resp.Content.Headers.ContentType;
                    var ce = resp.Content.Headers.ContentEncoding;
                    resp.Content.Dispose();
                    var fresh = new ByteArrayContent(bytes);
                    if (ct != null) fresh.Headers.ContentType = ct;
                    foreach (var enc in ce) fresh.Headers.ContentEncoding.Add(enc);
                    resp.Content = fresh;
                }
            }
            catch { /* best-effort */ }
        }
        return resp;
    }
}
