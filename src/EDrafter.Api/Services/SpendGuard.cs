using EDrafter.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Services;

/// <summary>
/// Brakes on real spending, on top of the ledger's idempotency guard.
///
///   1. ARMING   — pointing at the live API is not enough. ArmSpending must also be true,
///                 and it is false in every checked-in config.
///   2. CEILING  — optional cap on the running total of every successful debit.
///                 OFF by default (MaxTotalSpendPaise = 0): users may order as many as they
///                 need. Set a positive value to cap total spend again.
///
/// The idempotency ledger still guarantees one agreement can never be charged twice.
/// </summary>
public sealed class SpendGuard(
    AppDbContext db,
    IConfiguration config,
    ILogger<SpendGuard> logger)
{
    private bool IsLive => string.Equals(
        config["EDrafter:Mode"], "Live", StringComparison.OrdinalIgnoreCase);

    private bool IsArmed => config.GetValue("EDrafter:ArmSpending", false);

    private long MaxTotalSpendPaise => config.GetValue("EDrafter:MaxTotalSpendPaise", 0L);

    /// <summary>
    /// Returns null when the spend may proceed, or a human-readable reason when it may not.
    /// </summary>
    public async Task<string?> CheckAsync(SpendKind kind, long amountPaise, CancellationToken ct = default)
    {
        // Zoho has no mock: every Zoho call is real, so it has its own switch and does not
        // inherit eDrafter's Mode. Nothing is submitted until Zoho:ArmSpending is true.
        if (kind == SpendKind.ZohoEsign)
        {
            if (config.GetValue("Zoho:ArmSpending", false)) return null;

            return "BLOCKED: submitting to Zoho Sign consumes credits and emails the signers, but " +
                   "Zoho:ArmSpending is false. The free draft was created — open it in Zoho Sign to " +
                   "check the signature boxes, then set Zoho:ArmSpending to true deliberately and send again.";
        }

        // Against the mock, nothing real can be spent — let it through.
        if (!IsLive) return null;

        if (!IsArmed)
        {
            return $"BLOCKED: {kind} would spend ₹{amountPaise / 100m:0.00} against the LIVE API, " +
                   "but EDrafter:ArmSpending is false. Set it to true deliberately to allow spending.";
        }

        // A ceiling of 0 (or below) disables the budget check entirely. The arming switch
        // and the idempotency ledger still apply — this only removes the running total cap.
        var spentSoFar = await TotalSpentPaiseAsync(ct);
        if (MaxTotalSpendPaise > 0 && spentSoFar + amountPaise > MaxTotalSpendPaise)
        {
            return $"BLOCKED: {kind} would spend ₹{amountPaise / 100m:0.00}, bringing the total to " +
                   $"₹{(spentSoFar + amountPaise) / 100m:0.00}, over the ceiling of " +
                   $"₹{MaxTotalSpendPaise / 100m:0.00} (EDrafter:MaxTotalSpendPaise).";
        }

        logger.LogWarning(
            "LIVE SPEND ARMED: {Kind} for ₹{Amount:0.00}. Spent so far ₹{Spent:0.00} of ₹{Cap:0.00}.",
            kind, amountPaise / 100m, spentSoFar / 100m, MaxTotalSpendPaise / 100m);

        return null;
    }

    public async Task RecordSpendAsync(long amountPaise, CancellationToken ct = default)
    {
        if (!IsLive) return;
        var total = await TotalSpentPaiseAsync(ct);
        logger.LogWarning(
            "Live spend recorded. Running total ₹{Total:0.00} of ₹{Cap:0.00}.",
            (total) / 100m, MaxTotalSpendPaise / 100m);
    }

    /// <summary>
    /// Sums every debit we believe succeeded. Adopted counts too — reconciliation
    /// confirmed the money moved.
    /// </summary>
    public async Task<long> TotalSpentPaiseAsync(CancellationToken ct = default) =>
        await db.SpendAttempts
            .Where(a => a.Status == SpendStatus.Succeeded || a.Status == SpendStatus.Adopted)
            .SumAsync(a => a.AmountPaise, ct);

    public async Task<SpendSummary> SummaryAsync(CancellationToken ct = default)
    {
        var attempts = await db.SpendAttempts.ToListAsync(ct);
        var spent = attempts
            .Where(a => a.Status is SpendStatus.Succeeded or SpendStatus.Adopted)
            .Sum(a => a.AmountPaise);

        return new SpendSummary(
            Mode: IsLive ? "Live" : "Mock",
            Armed: IsArmed,
            TotalSpentPaise: spent,
            CeilingPaise: MaxTotalSpendPaise,
            SucceededCount: attempts.Count(a => a.Status is SpendStatus.Succeeded or SpendStatus.Adopted),
            UnknownCount: attempts.Count(a => a.Status == SpendStatus.Unknown),
            NeedsReviewCount: attempts.Count(a => a.Status is SpendStatus.Unknown or SpendStatus.Attempting));
    }
}

public sealed record SpendSummary(
    string Mode,
    bool Armed,
    long TotalSpentPaise,
    long CeilingPaise,
    int SucceededCount,
    int UnknownCount,
    int NeedsReviewCount);
