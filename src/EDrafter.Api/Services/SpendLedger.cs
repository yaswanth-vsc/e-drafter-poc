using EDrafter.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Services;

/// <summary>
/// Guards every call that can move money.
///
/// The contract is deliberately narrow: you cannot make a spending call except through
/// <see cref="ExecuteAsync"/>, and that method refuses to run twice for the same
/// idempotency key. The UNIQUE index does the enforcing, so this holds even across
/// concurrent requests, multiple instances, and process restarts — it is not a lock
/// and not a convention.
///
/// The outcome taxonomy matters as much as the guard:
///   2xx      -> Succeeded
///   4xx      -> FailedSafe   (rejected; no money moved; safe to correct and retry)
///   timeout  -> Unknown      (money MAY have moved; never auto-retry)
///   5xx      -> Unknown      (ditto)
/// </summary>
public sealed class SpendLedger(
    AppDbContext db,
    SpendGuard guard,
    ILogger<SpendLedger> logger)
{
    /// <summary>
    /// Runs a spending call exactly once for a given idempotency key.
    /// Returns <see cref="SpendOutcome{T}"/> describing what actually happened —
    /// callers must handle Unknown explicitly rather than treating it as failure.
    /// </summary>
    public async Task<SpendOutcome<T>> ExecuteAsync<T>(
        string idempotencyKey,
        SpendKind kind,
        long amountPaise,
        Guid? agreementId,
        string? ourRefId,
        Func<CancellationToken, Task<SpendCallResult<T>>> call,
        CancellationToken ct = default)
        where T : class
    {
        // 1. Has this exact operation already been attempted?
        var existing = await db.SpendAttempts
            .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, ct);

        if (existing is not null)
        {
            logger.LogWarning(
                "Spend blocked: idempotency key {Key} already exists with status {Status}. " +
                "Not calling eDrafter again.", idempotencyKey, existing.Status);

            return SpendOutcome<T>.AlreadyAttempted(existing);
        }

        // 2. Budget and arming checks, before anything is written.
        var blocked = await guard.CheckAsync(kind, amountPaise, ct);
        if (blocked is not null)
        {
            logger.LogError("Spend blocked by guard: {Reason}", blocked);
            return SpendOutcome<T>.Blocked(blocked);
        }

        // 3. Write the intent row FIRST. If we crash after this, the row survives and
        //    reconciliation will find it — which is the entire point.
        var attempt = new SpendAttempt
        {
            IdempotencyKey = idempotencyKey,
            Kind = kind,
            Status = SpendStatus.Attempting,
            AmountPaise = amountPaise,
            OurRefId = ourRefId,
            AgreementId = agreementId
        };
        db.SpendAttempts.Add(attempt);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent request for the same key.
            // The UNIQUE index caught it. This is the guard working as designed.
            db.Entry(attempt).State = EntityState.Detached;
            var winner = await db.SpendAttempts
                .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, ct);

            logger.LogWarning(
                "Concurrent spend attempt for key {Key} rejected by unique constraint.",
                idempotencyKey);

            return winner is not null
                ? SpendOutcome<T>.AlreadyAttempted(winner)
                : SpendOutcome<T>.Blocked("Concurrent attempt; could not resolve winner");
        }

        // 4. Make the call. No retry policy — see EDrafterClient registration.
        SpendCallResult<T> result;
        try
        {
            result = await call(ct);
        }
        catch (TaskCanceledException ex)
        {
            // The dangerous one: we do not know whether eDrafter processed it.
            await ResolveAsync(attempt, SpendStatus.Unknown, null, $"Timeout: {ex.Message}", ct);
            logger.LogError(ex,
                "UNKNOWN outcome for {Kind} (key {Key}). Money may have moved. " +
                "Reconcile — do NOT retry.", kind, idempotencyKey);
            return SpendOutcome<T>.Unknown(attempt);
        }
        catch (HttpRequestException ex)
        {
            await ResolveAsync(attempt, SpendStatus.Unknown, null, $"Transport: {ex.Message}", ct);
            logger.LogError(ex,
                "UNKNOWN outcome for {Kind} (key {Key}). Reconcile — do NOT retry.",
                kind, idempotencyKey);
            return SpendOutcome<T>.Unknown(attempt);
        }

        // 5. Record the outcome.
        if (result.IsSuccess)
        {
            await ResolveAsync(attempt, SpendStatus.Succeeded, result.EdrafterId, null, ct);
            await guard.RecordSpendAsync(amountPaise, ct);
            logger.LogInformation(
                "SPEND OK: {Kind} {Amount} paise, eDrafter id {Id}",
                kind, amountPaise, result.EdrafterId);
            return SpendOutcome<T>.Succeeded(attempt, result.Value!);
        }

        if (result.IsClientError)
        {
            // 4xx — eDrafter rejected it outright. No money moved.
            await ResolveAsync(attempt, SpendStatus.FailedSafe, null, result.Error, ct);
            logger.LogWarning("{Kind} rejected (safe, no charge): {Error}", kind, result.Error);
            return SpendOutcome<T>.FailedSafe(attempt, result.Error ?? "Rejected");
        }

        // 5xx — indeterminate.
        await ResolveAsync(attempt, SpendStatus.Unknown, null, result.Error, ct);
        logger.LogError("UNKNOWN outcome for {Kind}: {Error}. Reconcile — do NOT retry.",
            kind, result.Error);
        return SpendOutcome<T>.Unknown(attempt);
    }

    private async Task ResolveAsync(
        SpendAttempt attempt, SpendStatus status, string? edrafterId, string? reason, CancellationToken ct)
    {
        attempt.Status = status;
        attempt.EdrafterId = edrafterId;
        attempt.FailureReason = reason;
        attempt.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks an Unknown attempt as resolved after reconciliation found the real outcome.
    /// Called by the reconciler, never automatically by a retry.
    /// </summary>
    public async Task AdoptAsync(long attemptId, string edrafterId, CancellationToken ct = default)
    {
        var attempt = await db.SpendAttempts.FindAsync([attemptId], ct);
        if (attempt is null) return;

        attempt.Status = SpendStatus.Adopted;
        attempt.EdrafterId = edrafterId;
        attempt.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Reconciled: attempt {Id} adopted eDrafter id {EdrafterId}", attemptId, edrafterId);
    }

    public async Task<List<SpendAttempt>> GetUnresolvedAsync(CancellationToken ct = default) =>
        await db.SpendAttempts
            .Where(a => a.Status == SpendStatus.Unknown || a.Status == SpendStatus.Attempting)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
}

/// <summary>What the HTTP layer reports back to the ledger.</summary>
public sealed record SpendCallResult<T>(
    bool IsSuccess,
    bool IsClientError,
    T? Value,
    string? EdrafterId,
    string? Error) where T : class
{
    public static SpendCallResult<T> Ok(T value, string? edrafterId) =>
        new(true, false, value, edrafterId, null);

    public static SpendCallResult<T> ClientError(string error) =>
        new(false, true, null, null, error);

    public static SpendCallResult<T> ServerError(string error) =>
        new(false, false, null, null, error);
}

public sealed record SpendOutcome<T>(
    SpendResultKind Kind,
    SpendAttempt? Attempt,
    T? Value,
    string? Message) where T : class
{
    public static SpendOutcome<T> Succeeded(SpendAttempt a, T v) => new(SpendResultKind.Succeeded, a, v, null);
    public static SpendOutcome<T> FailedSafe(SpendAttempt a, string m) => new(SpendResultKind.FailedSafe, a, default, m);
    public static SpendOutcome<T> Unknown(SpendAttempt a) => new(SpendResultKind.Unknown, a, default,
        "Outcome unknown. Reconcile before any resend — never retry automatically.");
    public static SpendOutcome<T> AlreadyAttempted(SpendAttempt a) => new(SpendResultKind.AlreadyAttempted, a, default,
        $"This operation was already attempted (status: {a.Status}). Not calling again.");
    public static SpendOutcome<T> Blocked(string m) => new(SpendResultKind.Blocked, null, default, m);
}

public enum SpendResultKind
{
    Succeeded,
    FailedSafe,
    Unknown,
    AlreadyAttempted,
    Blocked
}
