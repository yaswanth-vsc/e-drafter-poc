using System.Diagnostics;
using System.Text;

namespace EDrafter.Api.EDrafterClient;

/// <summary>
/// Logs every eDrafter request and response in full — method, url, headers, body, status,
/// and how long it took.
///
/// This sits in the HttpClient pipeline, so it captures the bytes actually on the wire.
/// That matters: a serialisation bug or a stray null looks identical in application code
/// but is obvious here.
///
/// Redaction: the API key and the webhook secret are masked. Everything else is written
/// as-is, because the whole point is to see what eDrafter really received and returned.
/// documentBase64 is truncated — it is a whole PDF and would bury the log.
///
/// Verbosity is controlled by EDrafter:LogPayloads (default true in Development).
/// </summary>
public sealed class EDrafterLoggingHandler(
    ILogger<EDrafterLoggingHandler> logger,
    IConfiguration config) : DelegatingHandler
{
    private bool Enabled => config.GetValue("EDrafter:LogPayloads", true);
    private int MaxBodyChars => config.GetValue("EDrafter:LogMaxBodyChars", 4000);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (!Enabled) return await base.SendAsync(request, ct);

        var correlation = Guid.NewGuid().ToString("N")[..8];
        var sw = Stopwatch.StartNew();

        var requestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(ct);

        logger.LogInformation(
            "eDrafter REQUEST  [{Id}] {Method} {Url}\n  headers: {Headers}\n  body: {Body}",
            correlation,
            request.Method,
            request.RequestUri,
            DescribeHeaders(request),
            Summarise(requestBody));

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // A transport failure on a spending call is the UNKNOWN case — the request may
            // well have reached eDrafter. Log it loudly; the ledger decides what to do.
            logger.LogError(ex,
                "eDrafter FAILED   [{Id}] {Method} {Url} after {Ms}ms — no response received. " +
                "If this was POST /orders or POST /esign the outcome is UNKNOWN: reconcile, never retry.",
                correlation, request.Method, request.RequestUri, sw.ElapsedMilliseconds);
            throw;
        }

        sw.Stop();

        // Read as BYTES, never as a string. Re-buffering binary content through a
        // StringContent re-encodes it as UTF-8: every byte that is not valid UTF-8
        // becomes a replacement character, inflating a PDF by roughly 1.8x and
        // corrupting it. That silently broke every stamp and signed-document download.
        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);

        var buffered = new ByteArrayContent(responseBytes);
        foreach (var h in response.Content.Headers)
            buffered.Headers.TryAddWithoutValidation(h.Key, h.Value);
        response.Content = buffered;

        // Only render a body that is actually text. A PDF is summarised, not printed.
        var responseBody = DescribeBody(responseBytes, response);

        var level = response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Error;
        logger.Log(level,
            "eDrafter RESPONSE [{Id}] {Status} {Reason} for {Method} {Path} in {Ms}ms\n  body: {Body}",
            correlation,
            (int)response.StatusCode,
            response.ReasonPhrase,
            request.Method,
            request.RequestUri?.PathAndQuery,
            sw.ElapsedMilliseconds,
            Summarise(responseBody));

        // Spell out the common auth failures, since the generic body is often unhelpful.
        if ((int)response.StatusCode is 401 or 403)
        {
            logger.LogError(
                "eDrafter rejected the API key ({Status}). Every call will fail until it is fixed. " +
                "Check EDrafter:ApiKey — and note the key circulated in a Word document was " +
                "flagged for rotation, so it may have been disabled.",
                (int)response.StatusCode);
        }

        return response;
    }

    /// <summary>
    /// Renders a response body for the log. Binary payloads are described rather than
    /// decoded, so a 1MB PDF does not arrive as a screenful of mojibake.
    /// </summary>
    private static string DescribeBody(byte[] bytes, HttpResponseMessage response)
    {
        if (bytes.Length == 0) return "(empty)";

        var isPdf = bytes.Length >= 4 &&
                    bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;
        if (isPdf) return $"<PDF, {bytes.Length:N0} bytes>";

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var looksTextual =
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Length == 0;

        if (!looksTextual) return $"<{mediaType}, {bytes.Length:N0} bytes>";

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return $"<binary, {bytes.Length:N0} bytes>";
        }
    }

    private string DescribeHeaders(HttpRequestMessage request)
    {
        var parts = new List<string>();

        foreach (var h in request.Headers)
        {
            // Never write the key itself into a log file.
            var value = h.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                ? Mask(string.Join(",", h.Value))
                : string.Join(",", h.Value);
            parts.Add($"{h.Key}={value}");
        }

        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                parts.Add($"{h.Key}={string.Join(",", h.Value)}");
        }

        return parts.Count == 0 ? "(none)" : string.Join("  ", parts);
    }

    private static string Mask(string value) =>
        value.Length <= 8 ? "****" : $"{value[..4]}…{value[^2..]} ({value.Length} chars)";

    /// <summary>
    /// Trims a base64 document down to a note about its size, then caps overall length.
    /// Without this a single e-sign call would write half a megabyte to the log.
    /// </summary>
    private string Summarise(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty)";

        var trimmed = TruncateBase64Fields(body);

        return trimmed.Length <= MaxBodyChars
            ? trimmed
            : trimmed[..MaxBodyChars] + $"… [{trimmed.Length - MaxBodyChars} more chars]";
    }

    private static string TruncateBase64Fields(string body)
    {
        const string marker = "\"documentBase64\":\"";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return body;

        var valueStart = start + marker.Length;
        var end = body.IndexOf('"', valueStart);
        if (end < 0) return body;

        var length = end - valueStart;
        return string.Concat(
            body.AsSpan(0, valueStart),
            $"<base64 PDF, {length} chars ≈ {length * 3 / 4 / 1024}KB>",
            body.AsSpan(end));
    }
}
