using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EDrafter.Api.Services;

namespace EDrafter.Api.EDrafterClient;

/// <summary>
/// The only thing in the app that talks to eDrafter.
///
/// Read-only calls are free and may be retried freely. The two spending calls —
/// CreateOrderAsync and CreateEsignAsync — return a <see cref="SpendCallResult{T}"/>
/// and are never retried here: they classify the outcome and hand it to the ledger,
/// which decides. A retry policy on those is exactly the bug that double-charges.
/// </summary>
public sealed class EDrafterApiClient(HttpClient http, ILogger<EDrafterApiClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Free reads -------------------------------------------------------

    public Task<AccountInfo?> GetAccountAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<AccountInfo>("me", Json, ct);

    public Task<List<ProductDto>?> GetProductsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<ProductDto>>("products", Json, ct);

    public Task<StateRules?> GetStateRulesAsync(string state, string? articleCode, CancellationToken ct = default)
    {
        var url = $"states/{Uri.EscapeDataString(state)}/rules";
        if (!string.IsNullOrWhiteSpace(articleCode))
        {
            // Both, deliberately. ?articleCode= is documented but leaves the constraint
            // "pending"; ?article= is what actually resolves it. Verified in production.
            var encoded = Uri.EscapeDataString(articleCode);
            url += $"?articleCode={encoded}&article={encoded}";
        }
        return http.GetFromJsonAsync<StateRules>(url, Json, ct);
    }

    /// <summary>
    /// Free dry-run. Throws on a transport-level failure (a bad key, a 5xx) rather than
    /// reporting it as "invalid": an auth failure deserialises into an empty
    /// ValidateResponse, which would otherwise surface as "Validation failed:" with no
    /// reason and send the caller hunting through their own payload.
    /// </summary>
    public async Task<ValidateResponse?> ValidateOrderAsync(ValidateOrderRequest req, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("validate/order", req, Json, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            logger.LogError(
                "validate/order returned {Status}. This is NOT a field-validation failure — " +
                "the call itself was rejected. Body: {Body}", (int)res.StatusCode, body);

            throw new EDrafterApiException(
                (int)res.StatusCode,
                (int)res.StatusCode is 401 or 403
                    ? $"eDrafter rejected the API key ({(int)res.StatusCode}). Check EDrafter:ApiKey."
                    : $"eDrafter returned {(int)res.StatusCode} for validate/order: {Trim(body)}");
        }

        return JsonSerializer.Deserialize<ValidateResponse>(body, Json);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public async Task<QuoteResponse?> GetQuoteAsync(QuoteRequest req, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("orders/quote", req, Json, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<QuoteResponse>(Json, ct);
    }

    public Task<OrderDto?> GetOrderAsync(int idd, CancellationToken ct = default) =>
        http.GetFromJsonAsync<OrderDto>($"orders/{idd}", Json, ct);

    /// <summary>
    /// The recovery handle. After an UNKNOWN outcome on POST /orders, we look the order
    /// up by OUR reference instead of retrying the spend.
    /// </summary>
    public async Task<OrderListResponse?> FindOrdersByRefAsync(string refId, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"orders/by-ref/{Uri.EscapeDataString(refId)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return new OrderListResponse { Count = 0 };
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<OrderListResponse>(Json, ct);
    }

    public Task<StampListResponse?> GetOrderStampsAsync(int idd, CancellationToken ct = default) =>
        http.GetFromJsonAsync<StampListResponse>($"orders/{idd}/stamps", Json, ct);

    public async Task<byte[]> DownloadStampAsync(int idd, int stampId, CancellationToken ct = default) =>
        await http.GetByteArrayAsync($"orders/{idd}/stamps/{stampId}/download", ct);

    public Task<EsignStatusResponse?> GetEsignStatusAsync(string documentId, CancellationToken ct = default) =>
        http.GetFromJsonAsync<EsignStatusResponse>($"esign/{documentId}", Json, ct);

    /// <summary>Newest first — used to reconcile an UNKNOWN e-sign outcome.</summary>
    public Task<EsignListResponse?> ListEsignDocumentsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<EsignListResponse>("esign", Json, ct);

    /// <summary>
    /// Fetches the completed, signed document.
    ///
    /// Despite being documented as "Download the signed PDF", GET /esign/:id/signed
    /// returns a JSON ENVELOPE, not the file:
    ///
    ///   {"documentId":"...","status":"completed",
    ///    "signedUrl":"https://edrafterb2b.in/uploads/signed-....pdf"}
    ///
    /// Taking it at its word archives a ~179-byte JSON file named .pdf. So: read the
    /// response, and if it is JSON, follow signedUrl. Raw bytes are still handled in
    /// case they change it back — the check is on the content, not on a content-type
    /// header we would rather not trust.
    /// Verified against production 2026-09-16.
    /// </summary>
    public async Task<byte[]> DownloadSignedAsync(string documentId, CancellationToken ct = default)
    {
        var first = await http.GetByteArrayAsync($"esign/{documentId}/signed", ct);

        if (LooksLikePdf(first))
        {
            logger.LogInformation(
                "Signed document for {DocumentId} returned directly ({Bytes} bytes).",
                documentId, first.Length);
            return first;
        }

        var body = Encoding.UTF8.GetString(first);
        string? signedUrl = null;
        try
        {
            signedUrl = JsonSerializer.Deserialize<EsignSignedEnvelope>(body, Json)?.SignedUrl;
        }
        catch (JsonException)
        {
            // Neither a PDF nor parseable JSON — fall through to the error below.
        }

        if (string.IsNullOrWhiteSpace(signedUrl))
        {
            throw new EDrafterApiException(200,
                $"GET /esign/{documentId}/signed returned neither a PDF nor a signedUrl. " +
                $"Body: {(body.Length <= 300 ? body : body[..300] + "…")}");
        }

        logger.LogInformation(
            "Signed document for {DocumentId} is behind an envelope; following signedUrl.",
            documentId);

        // signedUrl is absolute and on their uploads host, so it goes out on a bare
        // request rather than through the API client's BaseAddress and key header.
        using var request = new HttpRequestMessage(HttpMethod.Get, signedUrl);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var pdf = await response.Content.ReadAsByteArrayAsync(ct);

        if (!LooksLikePdf(pdf))
        {
            throw new EDrafterApiException((int)response.StatusCode,
                $"signedUrl for {documentId} did not return a PDF ({pdf.Length} bytes).");
        }

        logger.LogInformation(
            "Signed document for {DocumentId} downloaded: {Bytes} bytes.", documentId, pdf.Length);
        return pdf;
    }

    /// <summary>Checks the magic bytes rather than trusting a content-type header.</summary>
    private static bool LooksLikePdf(byte[] data) =>
        data.Length >= 4 && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46;

    // ---- Webhook management (free) ----------------------------------------

    public async Task<List<WebhookDto>> ListWebhooksAsync(CancellationToken ct = default)
    {
        var res = await http.GetFromJsonAsync<WebhookListResponse>("webhooks", Json, ct);
        return res?.Webhooks ?? [];
    }

    /// <summary>
    /// Registers a webhook and returns the secret, which eDrafter shows ONCE.
    /// In production this belongs in Key Vault; here it is held in memory for the
    /// lifetime of the process, which is all a local run needs.
    /// </summary>
    public async Task<RegisterWebhookResponse?> RegisterWebhookAsync(
        string url, string[] events, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("webhooks",
            new { url, events, description = "eDrafter POC listener" }, Json, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<RegisterWebhookResponse>(Json, ct);
    }

    // ---- Spending calls — NEVER retried ------------------------------------

    /// <summary>
    /// 💸 Debits the wallet. Called only through SpendLedger.ExecuteAsync.
    /// Classifies the outcome; it does not decide what to do about it.
    /// </summary>
    public async Task<SpendCallResult<CreateOrderResponse>> CreateOrderAsync(
        CreateOrderRequest req, CancellationToken ct = default)
    {
        logger.LogWarning("SPENDING CALL: POST /orders refId={RefId}", req.RefId);

        var res = await http.PostAsJsonAsync("orders", req, Json, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (res.IsSuccessStatusCode)
        {
            var parsed = JsonSerializer.Deserialize<CreateOrderResponse>(body, Json);
            return parsed is null
                ? SpendCallResult<CreateOrderResponse>.ServerError("Order created but response unparseable")
                : SpendCallResult<CreateOrderResponse>.Ok(parsed, parsed.OrderId.ToString());
        }

        // 4xx is safe: rejected, nothing charged. 5xx is not.
        return (int)res.StatusCode is >= 400 and < 500
            ? SpendCallResult<CreateOrderResponse>.ClientError($"{(int)res.StatusCode}: {body}")
            : SpendCallResult<CreateOrderResponse>.ServerError($"{(int)res.StatusCode}: {body}");
    }

    /// <summary>
    /// 💸 Debits the wallet AT LINK GENERATION, per signatory, non-refundable.
    /// Called only through SpendLedger.ExecuteAsync.
    /// </summary>
    public async Task<SpendCallResult<CreateEsignResponse>> CreateEsignAsync(
        CreateEsignRequest req, CancellationToken ct = default)
    {
        logger.LogWarning(
            "SPENDING CALL: POST /esign name='{Name}' signatories={Count} method={Method}",
            req.Name, req.Signatories.Count, req.SignMethod);

        var res = await http.PostAsJsonAsync("esign", req, Json, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (res.IsSuccessStatusCode)
        {
            var parsed = JsonSerializer.Deserialize<CreateEsignResponse>(body, Json);
            return parsed is null
                ? SpendCallResult<CreateEsignResponse>.ServerError("e-Sign sent but response unparseable")
                : SpendCallResult<CreateEsignResponse>.Ok(parsed, parsed.DocumentId);
        }

        return (int)res.StatusCode is >= 400 and < 500
            ? SpendCallResult<CreateEsignResponse>.ClientError($"{(int)res.StatusCode}: {body}")
            : SpendCallResult<CreateEsignResponse>.ServerError($"{(int)res.StatusCode}: {body}");
    }
}

/// <summary>An eDrafter call that failed at the transport level, not at field validation.</summary>
public sealed class EDrafterApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
