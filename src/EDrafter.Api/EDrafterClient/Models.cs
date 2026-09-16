using System.Text.Json.Serialization;

namespace EDrafter.Api.EDrafterClient;

// ---- Requests -------------------------------------------------------------

public sealed class QuoteRequest
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public decimal Denomination { get; set; }
    public bool DoorstepDelivery { get; set; }
}

public sealed class CreateOrderRequest
{
    public string FirstParty { get; set; } = "";
    public string SecondParty { get; set; } = "";
    public string PurchasedBy { get; set; } = "";
    public string DutyPaidBy { get; set; } = "";
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? ArticleCode { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal Denomination { get; set; }
    public decimal? ConsiderationPrice { get; set; }
    public bool DoorstepDelivery { get; set; }
    public string RefId { get; set; } = "";
}

public sealed class CreateEsignRequest
{
    public string Name { get; set; } = "";
    public string SignMethod { get; set; } = "phone_otp";
    public List<EsignSignatoryDto> Signatories { get; set; } = new();
    public int? OrderId { get; set; }
    public int? StampId { get; set; }
    public string? DocumentBase64 { get; set; }
    public string? DocumentName { get; set; }
    public string? Reason { get; set; }
    public int? ExpiryDays { get; set; }
    public string? SignaturePosition { get; set; }
    public int? SignaturePage { get; set; }
}

public sealed class EsignSignatoryDto
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
}

// ---- Responses ------------------------------------------------------------

public sealed class AccountInfo
{
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public decimal Balance { get; set; }
}

public sealed class QuoteResponse
{
    public decimal StampValue { get; set; }
    public decimal ServiceCharge { get; set; }
    public decimal Shipping { get; set; }
    public decimal Gst { get; set; }
    public decimal Total { get; set; }
    public decimal WalletBalance { get; set; }
    public bool Affordable { get; set; }
}

public sealed class ValidateResponse
{
    public bool Valid { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class CreateOrderResponse
{
    public int OrderId { get; set; }
    public string? Reference { get; set; }
    public string? RefId { get; set; }
    public decimal TotalAmount { get; set; }
    public OrderSummary? Order { get; set; }
}

public sealed class OrderSummary
{
    [JsonPropertyName("_idd")] public int Idd { get; set; }
    public string? DisplayId { get; set; }
    public string Status { get; set; } = "";
}

public sealed class OrderDto
{
    [JsonPropertyName("_idd")] public int Idd { get; set; }
    public string? DisplayId { get; set; }
    public string? RefId { get; set; }

    /// <summary>
    /// eDrafter's order status. Their documentation lists only
    /// Pending / Processing / Completed / Cancelled, but their dashboard and the live
    /// API expose more: <c>Payment Pending</c>, <c>Requested</c>, <c>Hold</c> and
    /// <c>Shipped</c>. Treat this as an open string, never an enum — an unknown value
    /// must not throw. See EDrafterOrderStatus for the full set.
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>Set while Status is Hold. Shows the customer why.</summary>
    public string? HoldReason { get; set; }

    /// <summary>Where a held order returns to once released.</summary>
    public string? StatusBeforeHold { get; set; }

    /// <summary>Populated when eDrafter rejects an order outright — undocumented path.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>Its own lifecycle: 'paid', and others we have not yet observed.</summary>
    public string? PaymentStatus { get; set; }

    /// <summary>'none' when nothing was refunded.</summary>
    public string? RefundStatus { get; set; }

    public decimal TotalAmount { get; set; }
    public int StampCount { get; set; }
}

/// <summary>
/// The order statuses eDrafter actually uses, as seen in their dashboard.
/// Deliberately constants rather than an enum: an unrecognised value from the API must
/// flow through as data, not blow up deserialisation.
/// </summary>
public static class EDrafterOrderStatus
{
    /// <summary>Awaiting payment. The order exists but is not yet funded.</summary>
    public const string PaymentPending = "Payment Pending";

    /// <summary>Submitted, waiting for a human at eDrafter to accept it. No SLA.</summary>
    public const string Requested = "Requested";

    /// <summary>Documented, and what we observe on a newly placed order.</summary>
    public const string Pending = "Pending";

    /// <summary>Accepted. The ~1 working hour starts here, not at placement.</summary>
    public const string Processing = "Processing";

    /// <summary>Paused by eDrafter. HoldReason says why; StatusBeforeHold says where it resumes.</summary>
    public const string Hold = "Hold";

    /// <summary>Stamp issued. GET /orders/:idd/stamps returns ready:true.</summary>
    public const string Completed = "Completed";

    /// <summary>Physical dispatch. Not reachable for us — we always send doorstepDelivery:false.</summary>
    public const string Shipped = "Shipped";

    public const string Cancelled = "Cancelled";

    /// <summary>Terminal states — no stamp is coming.</summary>
    public static bool IsTerminalFailure(string? status) =>
        string.Equals(status, Cancelled, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True while an order may still yield a stamp. Note the fallback: an unrecognised
    /// status is treated as still in flight, so a new status eDrafter adds does not
    /// cause us to abandon a paid order.
    /// </summary>
    public static bool IsInFlight(string? status) => !IsTerminalFailure(status);
}

public sealed class OrderListResponse
{
    public int Count { get; set; }
    public List<OrderDto> Orders { get; set; } = new();
}

public sealed class StampListResponse
{
    public int OrderId { get; set; }
    public string Status { get; set; } = "";

    /// <summary>
    /// eDrafter's explicit "the e-stamp is ready to attach" flag. This is the signal to
    /// trust — more reliable than matching on a status string, since their status set is
    /// larger than their documentation admits.
    /// </summary>
    public bool Ready { get; set; }

    public int StampCount { get; set; }
    public int UsedCount { get; set; }
    public int Count { get; set; }
    public List<StampDto> Stamps { get; set; } = new();
}

public sealed class StampDto
{
    /// <summary>Live API returns this as `id`; `stampId` appears on GET /stamps.</summary>
    [JsonPropertyName("id")] public int Id { get; set; }
    public int StampId { get; set; }

    /// <summary>Which id is populated depends on the endpoint, so take whichever is set.</summary>
    public int EffectiveStampId => StampId != 0 ? StampId : Id;

    public string CertificateNo { get; set; } = "";
    public decimal Denomination { get; set; }

    /// <summary>⚠️ Contains the API key as a query parameter. Never log or expose it.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>True once consumed by an e-sign. A used stamp cannot be attached again (409).</summary>
    public bool Used { get; set; }

    public DateTime? DownloadedAt { get; set; }
}

public sealed class CreateEsignResponse
{
    public string DocumentId { get; set; } = "";
    public string? Uid { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public List<EsignSignatoryResult> Signatories { get; set; } = new();
}

public sealed class EsignSignatoryResult
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "";
    public string? SignUrl { get; set; }
}

public sealed class EsignStatusResponse
{
    public string DocumentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public List<EsignSignatoryStatus> Signatories { get; set; } = new();
}

public sealed class EsignSignatoryStatus
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? SignedAt { get; set; }
}

public sealed class EsignListResponse
{
    public int Count { get; set; }
    public List<EsignDocSummary> Documents { get; set; } = new();
}

public sealed class EsignDocSummary
{
    public string DocumentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public int SignatoryCount { get; set; }
    public int SignedCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class StateRules
{
    public string State { get; set; } = "";
    public bool Known { get; set; }
    public string? StampType { get; set; }
    public DenominationConstraint? DenominationConstraint { get; set; }
    public string? DenominationHint { get; set; }
    public decimal GovSurchargePct { get; set; }
    public FieldRules Fields { get; set; } = new();
}

public sealed class DenominationConstraint
{
    public string? Type { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public string? Label { get; set; }
}

public sealed class FieldRules
{
    public int MaxFieldLength { get; set; } = 50;
    public bool DisallowSpecialChars { get; set; }
    public bool DisallowNumericInNames { get; set; }
    public bool ConsiderationPriceAllowed { get; set; }
    public bool SecondPartyNameRequired { get; set; }
    public bool PurchaserOnly { get; set; }
}

public sealed class ProductDto
{
    [JsonPropertyName("_id")] public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public bool Serviceable { get; set; }
}

public sealed class WebhookDto
{
    [JsonPropertyName("_id")] public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> Events { get; set; } = new();
    public bool Active { get; set; }
}

public sealed class WebhookListResponse
{
    public List<string> SupportedEvents { get; set; } = new();
    public List<WebhookDto> Webhooks { get; set; } = new();
}

public sealed class RegisterWebhookResponse
{
    public string? Message { get; set; }
    public RegisteredWebhook? Webhook { get; set; }
}

public sealed class RegisteredWebhook
{
    [JsonPropertyName("_id")] public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> Events { get; set; } = new();
    /// <summary>Shown once by eDrafter. Save it.</summary>
    public string Secret { get; set; } = "";
    public bool Active { get; set; }
}

/// <summary>
/// Payload for POST /validate/order.
///
/// Deliberately NOT the same shape as CreateOrderRequest: this endpoint takes
/// <c>state</c> or <c>productId</c> (camelCase), whereas POST /orders takes
/// <c>product_id</c> (snake_case). Sending the order payload here fails with
/// "Provide 'state' or 'productId'".
/// </summary>
public sealed class ValidateOrderRequest
{
    public string? State { get; set; }
    public string? ProductId { get; set; }
    public string? FirstParty { get; set; }
    public string? SecondParty { get; set; }
    public string? FirstPartyAddress { get; set; }
    public string? PurchasedBy { get; set; }
    public string? DutyPaidBy { get; set; }
    /// <summary>What eDrafter's documentation specifies. Verified as IGNORED by this endpoint.</summary>
    public string? ArticleCode { get; set; }

    /// <summary>
    /// What this endpoint actually honours. Sending only <c>articleCode</c> yields
    /// "Denomination is not valid for Karnataka. Pick an article first" even for a code
    /// straight out of GET /states/:state/articles. Verified against production.
    /// </summary>
    public string? Article { get; set; }

    public decimal? Denomination { get; set; }
    public decimal? ConsiderationPrice { get; set; }
}

/// <summary>
/// What GET /esign/:id/signed actually returns — an envelope pointing at the file,
/// not the file itself, despite the documentation calling it a download.
/// </summary>
public sealed class EsignSignedEnvelope
{
    public string DocumentId { get; set; } = "";
    public string? Uid { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }

    /// <summary>Absolute URL on eDrafter's uploads host. Follow it for the real PDF.</summary>
    public string? SignedUrl { get; set; }
}
