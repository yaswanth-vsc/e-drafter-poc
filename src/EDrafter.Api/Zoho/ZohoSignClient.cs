using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace EDrafter.Api.Zoho;

/// <summary>
/// Every Zoho Sign call this app makes. Signing only — the e-stamp comes from eDrafter,
/// so nothing here ever sends an estamping_request (that would buy a second stamp).
///
/// Three traps carried over from the Zoho POC:
///   1. Writes are form-encoded with the whole JSON in ONE field named "data". The one
///      exception is the upload, which is multipart: "file" plus the same "data" field.
///   2. HTTP 200 with "status":"failure" is a failure.
///   3. There is deliberately NO retry policy. /submit consumes credits and is not
///      idempotent; the only retry is one repeat after a 401, which Zoho rejected before
///      doing anything.
///
/// Timeouts and transport errors are NOT caught here — the spend ledger has to see them
/// to record the outcome as Unknown rather than as a failure.
/// </summary>
public sealed class ZohoSignClient(
    HttpClient http,
    IZohoTokenProvider tokens,
    IOptions<ZohoOptions> options,
    ILogger<ZohoSignClient> log)
{
    private readonly ZohoOptions _o = options.Value;

    // ---- Create the draft (free) --------------------------------------------

    /// <summary>
    /// POST /requests — uploads the finished PDF and creates a DRAFT with one SIGN action
    /// per signer. Free: nothing is sent and nobody is emailed until /submit.
    ///
    /// is_sequential with signing_order makes Zoho notify the second party first and the
    /// first party only after the second party has signed. Delivery, signing method and
    /// recipient authentication all come from config — see <see cref="BuildAction"/>.
    /// </summary>
    public async Task<ZohoCreatedRequest> CreateRequestAsync(
        byte[] pdf, string fileName, string requestName, string notes,
        IReadOnlyList<ZohoSigner> signers, CancellationToken ct = default)
    {
        var actions = new JsonArray();
        foreach (var s in signers.OrderBy(s => s.SigningOrder))
            actions.Add(BuildAction(s));

        var data = new JsonObject
        {
            ["requests"] = new JsonObject
            {
                ["request_name"] = requestName,
                ["is_sequential"] = true,
                ["expiration_days"] = _o.ExpiryDays,
                ["email_reminders"] = _o.EmailReminders,
                ["reminder_period"] = _o.ReminderPeriodDays,
                ["notes"] = notes,
                ["actions"] = actions
            }
        };

        var json = data.ToJsonString();

        // Test mode goes on the create as well as the submit — the same pairing the Zoho
        // POC used on its two billable calls.
        var createUrl = _o.TestMode ? "requests?testing=true" : "requests";

        var node = await SendAsync(HttpMethod.Post, createUrl, () =>
        {
            var file = new ByteArrayContent(pdf);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            return new MultipartFormDataContent
            {
                { file, "file", fileName },
                { new StringContent(json), "data" }
            };
        }, $"data={json}  (+ file {fileName}, {pdf.Length} bytes)", ct);

        var requests = node["requests"] ?? throw new ZohoException("POST /requests: no requests node in the response.");

        var requestId = requests["request_id"]?.ToString()
            ?? throw new ZohoException("POST /requests: no request_id in the response.");
        var documentId = requests["document_ids"]?.AsArray().FirstOrDefault()?["document_id"]?.ToString()
            ?? throw new ZohoException("POST /requests: no document_id in the response.");

        return new ZohoCreatedRequest(requestId, documentId, ReadActions(requests));
    }

    // ---- Place the signature boxes (free) -----------------------------------

    /// <summary>
    /// PUT /requests/{id} — adds one Signature field per page per signer to the draft.
    /// Free. Call it once: a second PUT adds a second set of boxes.
    /// </summary>
    public async Task AddSignatureFieldsAsync(
        string requestId, string documentId,
        IReadOnlyList<ZohoSigner> signers, IReadOnlyList<ZohoSignatureField> fields,
        CancellationToken ct = default)
    {
        var actions = new JsonArray();

        foreach (var s in signers.OrderBy(s => s.SigningOrder))
        {
            if (string.IsNullOrWhiteSpace(s.ActionId))
                throw new ZohoException($"{s.Name} has no Zoho action_id — the draft was not read back correctly.");

            var list = new JsonArray();
            foreach (var f in fields.Where(f => f.ActionId == s.ActionId))
                list.Add(FieldJson(f, documentId));

            JsonNode fieldsNode = string.Equals(_o.FieldsShape, "grouped", StringComparison.OrdinalIgnoreCase)
                ? new JsonObject { ["image_fields"] = list }
                : list;

            actions.Add(new JsonObject
            {
                ["action_id"] = s.ActionId,
                ["action_type"] = "SIGN",
                ["recipient_name"] = s.Name,
                ["recipient_email"] = s.Email,
                ["signing_order"] = s.SigningOrder,
                ["fields"] = fieldsNode
            });
        }

        var json = new JsonObject { ["requests"] = new JsonObject { ["actions"] = actions } }.ToJsonString();

        await SendAsync(HttpMethod.Put, $"requests/{requestId}", () => Form(json), $"data={json}", ct);
    }

    // ---- Submit (💸 consumes credits) ----------------------------------------

    /// <summary>
    /// POST /requests/{id}/submit — the point of no return. Zoho emails the first signer
    /// (the second party) and credits are consumed. Called only through the spend ledger,
    /// never retried.
    ///
    /// The action ids are the ones Zoho minted for this draft, read back when it was
    /// created — the POC hit 4008 "Invalid action" by reusing ids from elsewhere.
    /// </summary>
    public async Task<JsonNode> SubmitAsync(
        string requestId, IReadOnlyList<ZohoSigner> signers, CancellationToken ct = default)
    {
        // The same action shape as create. Zoho's create reference does not list every key,
        // its cloud-signing guide shows them on requests.actions[] — sending them on both
        // calls is what makes the settings hold whichever call Zoho reads them from.
        var actions = new JsonArray();
        foreach (var s in signers.OrderBy(s => s.SigningOrder))
        {
            var action = BuildAction(s);
            action["action_id"] = s.ActionId;
            actions.Add(action);
        }

        var json = new JsonObject { ["requests"] = new JsonObject { ["actions"] = actions } }.ToJsonString();
        var query = _o.TestMode ? "?testing=true" : "";

        return await SendAsync(HttpMethod.Post, $"requests/{requestId}/submit{query}", () => Form(json), $"data={json}", ct);
    }

    // ---- Reads (free) ---------------------------------------------------------

    public async Task<ZohoRequestState> GetRequestStateAsync(string requestId, CancellationToken ct = default)
    {
        var node = await SendAsync(HttpMethod.Get, $"requests/{requestId}", null, null, ct);
        var requests = node["requests"] ?? throw new ZohoException($"GET /requests/{requestId}: no requests node.");
        return ReadState(requests);
    }

    /// <summary>Reads the request block of a GET response or a webhook payload.</summary>
    public static ZohoRequestState ReadState(JsonNode requests) => new(
        RequestId: requests["request_id"]?.ToString() ?? "",
        Status: requests["request_status"]?.ToString(),
        Actions: ReadActions(requests));

    /// <summary>
    /// The finished document: stamp + agreement + every signature + Zoho's completion
    /// certificate, as one PDF.
    /// </summary>
    public async Task<byte[]> DownloadSignedPdfAsync(string requestId, CancellationToken ct = default)
    {
        var url = $"requests/{requestId}/pdf?with_coc=true&merge=true";

        var bytes = await DownloadOnceAsync(url, ct);
        if (bytes is null)
        {
            tokens.Invalidate();
            bytes = await DownloadOnceAsync(url, ct);
        }

        return bytes ?? throw new ZohoException($"GET {url} returned 401 twice — the Zoho grant is not usable.", 401);
    }

    // ---- Plumbing -------------------------------------------------------------

    /// <summary>
    /// One SIGN action, built from config: delivery, signing method and recipient auth.
    ///
    ///   delivery_mode      EMAIL | EMAIL_SMS. "SMS" alone is rejected by Zoho (code 9013).
    ///   verification_type  EMAIL | SMS — an OTP to OPEN the document, NOT the signature.
    ///   allowed_cloud_..   [25] pins signing to Aadhaar eSign; omitted entirely for ZOHO,
    ///                      which leaves the signer Zoho's own signature.
    /// </summary>
    private JsonObject BuildAction(ZohoSigner s)
    {
        var action = new JsonObject
        {
            ["action_type"] = "SIGN",
            ["recipient_name"] = s.Name,
            ["recipient_email"] = s.Email,
            ["signing_order"] = s.SigningOrder,
            ["delivery_mode"] = _o.DeliveryMode,
            ["verify_recipient"] = _o.AuthEnabled
        };

        if (_o.AuthEnabled)
            action["verification_type"] = _o.RecipientAuth;

        if (_o.UsesAadhaar)
            action["allowed_cloud_provider_ids"] = ProviderIds();

        // Required by Zoho for SMS delivery and for an SMS OTP; harmless otherwise.
        if (!string.IsNullOrWhiteSpace(s.Phone))
        {
            action["recipient_phonenumber"] = s.Phone;
            action["recipient_countrycode_iso"] = _o.PhoneCountryIso;
        }

        return action;
    }

    private JsonArray ProviderIds()
    {
        var ids = new JsonArray();
        foreach (var id in _o.EffectiveCloudProviderIds) ids.Add(id);
        return ids;
    }

    private JsonObject FieldJson(ZohoSignatureField f, string documentId)
    {
        var field = new JsonObject
        {
            ["field_type_name"] = "Signature",
            ["field_category"] = "image",
            ["field_name"] = f.FieldName,
            ["field_label"] = f.FieldName,
            ["is_mandatory"] = true,
            ["document_id"] = documentId,
            ["page_no"] = f.PageNo    // zero-based, per Zoho's reference
        };

        if (string.Equals(_o.CoordinateMode, "percent", StringComparison.OrdinalIgnoreCase))
        {
            field["x_value"] = Percent(f.X, f.PageWidth);
            field["y_value"] = Percent(f.Y, f.PageHeight);
            field["width"] = Percent(f.W, f.PageWidth);
            field["height"] = Percent(f.H, f.PageHeight);
        }
        else
        {
            // Zoho documents these as integers.
            field["x_coord"] = (int)Math.Round(f.X);
            field["y_coord"] = (int)Math.Round(f.Y);
            field["abs_width"] = (int)Math.Round(f.W);
            field["abs_height"] = (int)Math.Round(f.H);
        }

        return field;
    }

    private static double Percent(double value, double whole) => Math.Round(value / whole * 100.0, 2);

    private static FormUrlEncodedContent Form(string json) =>
        new([new KeyValuePair<string, string>("data", json)]);

    private static List<ZohoAction> ReadActions(JsonNode requests)
    {
        var list = new List<ZohoAction>();
        foreach (var a in requests["actions"]?.AsArray() ?? [])
        {
            var id = a?["action_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) continue;

            int? order = int.TryParse(a?["signing_order"]?.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var o) ? o : null;

            list.Add(new ZohoAction(
                ActionId: id,
                Email: a?["recipient_email"]?.ToString() ?? "",
                SigningOrder: order,
                Status: a?["action_status"]?.ToString()));
        }
        return list;
    }

    private async Task<JsonNode> SendAsync(
        HttpMethod method, string url, Func<HttpContent>? content, string? logPayload, CancellationToken ct)
    {
        var (status, raw) = await AttemptAsync(method, url, content, logPayload, ct);

        // A 401 means the token died, not that the request was wrong — Zoho did nothing.
        // Exactly one retry with a fresh token, so a revoked grant fails fast.
        if (status == HttpStatusCode.Unauthorized)
        {
            tokens.Invalidate();
            (status, raw) = await AttemptAsync(method, url, content, logPayload, ct);
        }

        if ((int)status is < 200 or >= 300)
            throw new ZohoException($"{method} {url} failed with {(int)status}: {Trim(raw)}", (int)status, ReadCode(raw));

        JsonNode node;
        try
        {
            node = JsonNode.Parse(raw) ?? throw new ZohoException($"{method} {url} returned an empty body.", (int)status);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ZohoException($"{method} {url} returned a body that is not JSON: {Trim(raw)}", (int)status);
        }

        // Zoho's domain errors come back as HTTP 200 with status=failure.
        if (node["status"]?.ToString() == "failure")
            throw new ZohoException($"{method} {url} returned failure: {Trim(raw)}", (int)status, ReadCode(raw));

        return node;
    }

    private async Task<(HttpStatusCode Status, string Body)> AttemptAsync(
        HttpMethod method, string url, Func<HttpContent>? content, string? logPayload, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);

        using var req = new HttpRequestMessage(method, url) { Content = content?.Invoke() };
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);

        if (_o.LogPayloads)
            log.LogInformation("ZOHO → {Method} {Url}{Payload}", method, url,
                logPayload is null ? "" : "\n  " + Trim(logPayload));

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            log.LogInformation("ZOHO ← {Status} {Method} {Url} in {Ms:0}ms{Body}",
                (int)res.StatusCode, method, url, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                _o.LogPayloads ? "\n  " + Trim(raw) : "");

            return (res.StatusCode, raw);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            // No response at all. After /submit this is genuinely ambiguous — the ledger
            // records it as Unknown, which is why it is rethrown rather than wrapped.
            log.LogError(ex, "ZOHO ✗ {Method} {Url}: no response after {Ms:0}ms", method, url,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    /// <summary>The bytes, or null on a 401 worth one retry with a fresh token.</summary>
    private async Task<byte[]?> DownloadOnceAsync(string url, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        log.LogInformation("ZOHO ← {Status} GET {Url} ({Bytes} bytes)", (int)res.StatusCode, url, bytes.Length);

        if (!res.IsSuccessStatusCode)
            throw new ZohoException($"GET {url} failed with {(int)res.StatusCode}.", (int)res.StatusCode);

        // A JSON error body instead of a PDF — e.g. 9039 when the request is not finished.
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
            throw new ZohoException($"GET {url} did not return a PDF: {Trim(System.Text.Encoding.UTF8.GetString(bytes))}");

        return bytes;
    }

    private static int? ReadCode(string raw)
    {
        try { return int.TryParse(JsonNode.Parse(raw)?["code"]?.ToString(), out var c) ? c : null; }
        catch { return null; }
    }

    private string Trim(string s) =>
        s.Length <= _o.LogMaxBodyChars ? s : s[.._o.LogMaxBodyChars] + $"… ({s.Length} chars)";
}

/// <summary>
/// A Zoho call that got an answer, and the answer was no. StatusCode is null only when the
/// failure happened before any HTTP call (missing credentials).
/// </summary>
public sealed class ZohoException(string message, int? statusCode = null, int? code = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;

    /// <summary>Zoho's own error code, e.g. 9043 extra key, 4008 invalid action, 13001 no credits.</summary>
    public int? Code { get; } = code;

    /// <summary>5xx — Zoho may have done the work before failing. Everything else it did not.</summary>
    public bool IsServerError => StatusCode is >= 500;
}

/// <summary>One signer as Zoho sees them. ActionId is null until the draft exists.</summary>
public sealed record ZohoSigner(string Role, string Name, string Email, string? Phone, int SigningOrder, string? ActionId = null);

public sealed record ZohoAction(string ActionId, string Email, int? SigningOrder, string? Status);

public sealed record ZohoCreatedRequest(string RequestId, string DocumentId, IReadOnlyList<ZohoAction> Actions);

public sealed record ZohoRequestState(string RequestId, string? Status, IReadOnlyList<ZohoAction> Actions)
{
    public bool IsDraft => string.Equals(Status, "draft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A signature box in PDF points, top-left origin, zero-based page.</summary>
public sealed record ZohoSignatureField(
    string ActionId, string FieldName, int PageNo,
    double X, double Y, double W, double H, double PageWidth, double PageHeight);
