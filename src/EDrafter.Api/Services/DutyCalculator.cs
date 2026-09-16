namespace EDrafter.Api.Services;

/// <summary>
/// Stamp duty, per article.
///
/// The cap is deliberately PER ARTICLE, not global. For Karnataka 30(1)(i) the rule is
/// 0.5% of consideration capped at ₹500 — but that cap does NOT appear in eDrafter's API
/// rules for the article; it comes from their B2C product and is open with them (S1).
/// Because it is unconfirmed it lives in config, so changing it is a settings edit.
///
/// Applying one article's cap to every article would silently under-charge duty, so
/// articles without an explicit entry get no local cap at all — their limits come from
/// GET /states/:state/rules.
/// </summary>
public sealed class DutyCalculator(IConfiguration config, ILogger<DutyCalculator> logger)
{
    public DutyResult Calculate(string articleCode, decimal considerationAmount)
    {
        var section = config.GetSection($"DutyRules:{articleCode}");

        if (!section.Exists())
        {
            logger.LogWarning(
                "No local duty rule for article {Article}; caller must use the API's own limits.",
                articleCode);
            return new DutyResult(articleCode, considerationAmount, null, null, 0m, false,
                $"No local rule for article {articleCode} — use the denomination limits from GET /states/:state/rules.");
        }

        var pct = section.GetValue<decimal>("Pct");
        var cap = section.GetValue<decimal?>("CapRupees");

        var raw = Math.Round(considerationAmount * pct / 100m, 2, MidpointRounding.AwayFromZero);
        var capped = cap.HasValue && raw > cap.Value;
        var duty = capped ? cap!.Value : raw;

        if (capped)
        {
            logger.LogInformation(
                "Duty for {Article} capped: {Raw} -> {Capped} (cap ₹{Cap}, unconfirmed by eDrafter — S1)",
                articleCode, raw, duty, cap);
        }

        return new DutyResult(articleCode, considerationAmount, pct, cap, duty, capped, null);
    }
}

public sealed record DutyResult(
    string ArticleCode,
    decimal ConsiderationAmount,
    decimal? Pct,
    decimal? CapRupees,
    decimal Duty,
    bool WasCapped,
    string? Warning);
