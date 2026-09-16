namespace EDrafter.Api.Services;

/// <summary>
/// e-Sign cost, computed by us because there is NO quote endpoint for e-sign.
///
/// Two facts drive this and both are confirmed with eDrafter:
///   1. The charge is PER SIGNATORY, per document — linear in party count.
///   2. There is NO GST on e-sign. The 18% applies to the eDrafter service charge and
///      courier only. So the rates are the amounts actually debited.
///
/// The wallet is debited when the signing link is GENERATED, not when signing completes,
/// and a decline, cancellation or expiry refunds nothing. That is why the cost has to be
/// shown and confirmed before POST /esign is ever called.
/// </summary>
public sealed class EsignPricing(IConfiguration config)
{
    /// <summary>Rates in paise. No GST multiplier — deliberately absent.</summary>
    public long RatePaise(string signMethod) =>
        config.GetValue<long?>($"EsignRatesPaise:{signMethod}")
        ?? signMethod switch
        {
            "email_otp" => 1000,   // ₹10 — cheapest, but not a documented API value (E4)
            "phone_otp" => 1200,   // ₹12 — the POC default: documented and we collect phones
            "dsc" => 1500,         // ₹15
            "aadhaar_otp" => 2000, // ₹20
            _ => 1200
        };

    public EsignCost Compute(string signMethod, int signatoryCount)
    {
        var rate = RatePaise(signMethod);
        return new EsignCost(
            SignMethod: signMethod,
            SignatoryCount: signatoryCount,
            RatePaise: rate,
            TotalPaise: rate * signatoryCount);
    }
}

public sealed record EsignCost(string SignMethod, int SignatoryCount, long RatePaise, long TotalPaise)
{
    public decimal RateRupees => RatePaise / 100m;
    public decimal TotalRupees => TotalPaise / 100m;

    /// <summary>Wording for the confirmation dialog. Non-refundability is not a footnote.</summary>
    public string ConfirmationText =>
        $"This charges ₹{TotalRupees:0.00} now — ₹{RateRupees:0.00} × {SignatoryCount} " +
        $"signator{(SignatoryCount == 1 ? "y" : "ies")}. It is NOT refunded if a signatory " +
        "declines, the request is cancelled, or the document expires unsigned.";
}
