namespace EDrafter.MockServer;

/// <summary>
/// Mirrors eDrafter's documented cost composition, so our own calculator can be
/// checked against it for free before any live spend.
///
///   stampValue = quantity x denomination
///   service    = denomination &lt;= 1000 ? flat 55 per stamp : 10% of denomination per stamp
///   gst        = 18% of (service + shipping)          <- never on stamp duty
///   total      = stampValue + service + shipping + gst
/// </summary>
public static class Pricing
{
    public const decimal FlatServiceCharge = 55m;
    public const decimal HighDenominationThreshold = 1000m;
    public const decimal HighDenominationPct = 0.10m;
    public const decimal GstPct = 0.18m;
    public const decimal ShippingUpTo150 = 99m;

    public static QuoteResult Quote(int quantity, decimal denomination, bool doorstepDelivery)
    {
        var stampValue = quantity * denomination;

        // Above the threshold the 10% REPLACES the flat 55 - confirmed by eDrafter (Q3).
        // The threshold is tested against the per-stamp denomination, and the charge is per stamp.
        var servicePerStamp = denomination > HighDenominationThreshold
            ? denomination * HighDenominationPct
            : FlatServiceCharge;
        var serviceCharge = servicePerStamp * quantity;

        var shipping = doorstepDelivery ? ShippingForQuantity(quantity) : 0m;

        // GST applies to the service charge and shipping only - never to the stamp duty.
        var gst = Round2((serviceCharge + shipping) * GstPct);

        return new QuoteResult(
            StampValue: stampValue,
            ServiceCharge: Round2(serviceCharge),
            Shipping: shipping,
            Gst: gst,
            Total: Round2(stampValue + serviceCharge + shipping + gst));
    }

    private static decimal ShippingForQuantity(int quantity) => quantity switch
    {
        <= 150 => ShippingUpTo150,
        <= 500 => 300m,
        <= 800 => 700m,
        <= 1200 => 1000m,
        <= 5000 => 1500m,
        _ => 2500m
    };

    /// <summary>
    /// e-Sign: per signatory, per document. No GST - confirmed; the 18% applies to the
    /// service charge and courier only.
    /// </summary>
    public static decimal EsignCost(string signMethod, int signatoryCount)
        => EsignRate(signMethod) * signatoryCount;

    public static decimal EsignRate(string signMethod) => signMethod switch
    {
        "email_otp" => 10m,
        "phone_otp" => 12m,
        "dsc" => 15m,
        "aadhaar_otp" => 20m,
        _ => 12m
    };

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

public readonly record struct QuoteResult(
    decimal StampValue,
    decimal ServiceCharge,
    decimal Shipping,
    decimal Gst,
    decimal Total);
