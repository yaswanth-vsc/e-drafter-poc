using System.Text.Json.Serialization;

namespace EDrafter.MockServer;

public sealed class QuoteRequest
{
    [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal Denomination { get; set; }
    public bool? DoorstepDelivery { get; set; }
}

public sealed class OrderRequest
{
    public string? FirstParty { get; set; }
    public string? SecondParty { get; set; }
    public string? PurchasedBy { get; set; }
    public string? DutyPaidBy { get; set; }
    [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    public string? Purpose { get; set; }
    public string? ArticleCode { get; set; }
    public int Quantity { get; set; }
    public decimal Denomination { get; set; }
    public decimal? ConsiderationPrice { get; set; }
    public bool? DoorstepDelivery { get; set; }
    public string? RefId { get; set; }
}

public sealed class EsignRequest
{
    public string? Name { get; set; }
    public string? SignMethod { get; set; }
    public List<EsignSignatory>? Signatories { get; set; }
    public int? OrderId { get; set; }
    public int? StampId { get; set; }
    public string? DocumentBase64 { get; set; }
    public string? DocumentName { get; set; }
    public string? Reason { get; set; }
    public int? ExpiryDays { get; set; }
    public string? SignaturePosition { get; set; }
    public int? SignaturePage { get; set; }
}

public sealed class EsignSignatory
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
}

public sealed class WebhookRequest
{
    public string Url { get; set; } = "";
    public List<string>? Events { get; set; }
    public string? Description { get; set; }
}
