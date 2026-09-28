using EDrafter.Api.EDrafterClient;

namespace EDrafter.Api.Services;

/// <summary>
/// Stamp duty, from eDrafter.
///
/// This used to compute the percentage locally from a config rule. It no longer does.
/// eDrafter shipped POST /stamp-duty/calculate in September 2026, backed by the same
/// engine POST /orders uses, so asking them is the only way to be certain the duty we
/// show is the duty we are charged. A local percentage that drifts from theirs produces
/// an order priced differently from the quote the user accepted.
///
/// Which amount field an article wants is also eDrafter's to decide, not ours: Karnataka
/// 30(1)(i) is 0.5% of the RENTAL SECURITY and ignores considerationPrice entirely. The
/// rule returned alongside the figure says which field applies, so the caller never has
/// to guess.
///
/// The config DutyRules section remains as a LAST-RESORT fallback for articles eDrafter
/// does not auto-price, and is deliberately never preferred over their answer.
/// </summary>
public sealed class DutyCalculator(
    EDrafterApiClient client,
    IConfiguration config,
    ILogger<DutyCalculator> logger)
{
    private string State => config["Scope:State"] ?? "Karnataka";

    /// <summary>
    /// Asks eDrafter for the duty. Throws <see cref="StampDutyException"/> when they refuse
    /// the amount — that is a user-facing validation message, not a failure to handle here.
    /// </summary>
    public async Task<DutyResult> CalculateAsync(
        string articleCode, decimal amount, CancellationToken ct = default)
    {
        // Ask which field this article wants before sending one. 30(1)(i) returns no duty
        // at all if given considerationPrice, so guessing silently under-prices the order.
        var rule = await GetRuleAsync(articleCode, ct);
        var wantsRental = string.Equals(rule?.Requires, "rentalSecurity",
            StringComparison.OrdinalIgnoreCase);

        var req = new StampDutyCalcRequest
        {
            State = State,
            ArticleCode = articleCode,
            RentalSecurity = wantsRental ? amount : null,
            ConsiderationPrice = wantsRental ? null : amount
        };

        var res = await client.CalculateStampDutyAsync(req, ct);

        if (res is { AutoCalculated: true, StampDuty: { } duty })
        {
            logger.LogInformation(
                "Duty for {Article} from eDrafter: ₹{Duty} ({Basis} ₹{Amount}).",
                articleCode, duty, res.Basis, res.Amount);

            return new DutyResult(articleCode, amount, duty, res.Basis, res.Rule, true, null);
        }

        // Ruled, but they declined to price it — the message names the missing field.
        if (res is { AutoCalculated: true, Message: { Length: > 0 } message })
            throw new StampDutyException(message);

        return FallbackToConfig(articleCode, amount, res?.Rule);
    }

    /// <summary>
    /// The article's rule, so a form can state the basis and minimum before the user has
    /// typed an amount. A lookup failure is not fatal: the calculate call still answers.
    /// </summary>
    public async Task<StampDutyRule?> GetRuleAsync(string articleCode, CancellationToken ct = default)
    {
        try
        {
            var articles = await client.GetArticlesAsync(State, ct);
            return articles?.Articles
                .FirstOrDefault(a => string.Equals(a.Code, articleCode, StringComparison.OrdinalIgnoreCase))
                ?.StampDutyRule;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the rule for {Article}; continuing.", articleCode);
            return null;
        }
    }

    /// <summary>
    /// Only for articles eDrafter does not auto-price. Config is a stand-in for their
    /// answer, so it is loudly flagged — an order priced this way may not match theirs.
    /// </summary>
    private DutyResult FallbackToConfig(string articleCode, decimal amount, StampDutyRule? rule)
    {
        var section = config.GetSection($"DutyRules:{articleCode}");

        if (!section.Exists())
        {
            logger.LogWarning(
                "eDrafter does not auto-price {Article} and there is no local rule. " +
                "The denomination must be supplied by the caller.", articleCode);

            return new DutyResult(articleCode, amount, 0m, null, rule, false,
                $"eDrafter does not auto-calculate duty for article {articleCode}. " +
                "Enter the denomination manually, within the limits from GET /states/:state/rules.");
        }

        var pct = section.GetValue<decimal>("Pct");
        var cap = section.GetValue<decimal?>("CapRupees");
        var raw = Math.Round(amount * pct / 100m, 2, MidpointRounding.AwayFromZero);
        var duty = cap.HasValue && raw > cap.Value ? cap.Value : raw;

        logger.LogWarning(
            "Duty for {Article} computed LOCALLY as ₹{Duty} ({Pct}% of ₹{Amount}) because " +
            "eDrafter does not auto-price it. This may not match what the order is charged.",
            articleCode, duty, pct, amount);

        return new DutyResult(articleCode, amount, duty, "local-config", rule, false,
            $"Duty computed locally at {pct}% — eDrafter does not auto-price article {articleCode}.");
    }
}

public sealed record DutyResult(
    string ArticleCode,
    decimal Amount,
    decimal Duty,
    string? Basis,
    StampDutyRule? Rule,
    /// <summary>True when the figure came from eDrafter, false when computed locally.</summary>
    bool FromEDrafter,
    string? Warning);
