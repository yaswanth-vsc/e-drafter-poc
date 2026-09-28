using EDrafter.Api.Data;
using EDrafter.Api.EDrafterClient;
using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Services;

/// <summary>
/// Resolves UNKNOWN spend attempts — the timeouts and 5xx where money may or may not
/// have moved.
///
/// The rule is absolute: **never retry the spending call**. Instead read (free, safe)
/// and find out what actually happened:
///   order  -> GET /orders/by-ref/{our refId}
///   esign  -> GET /esign (newest first), match on document name
///
/// Found     -> adopt it; the spend happened, record it and move on.
/// Not found -> still do NOT auto-retry. Flag for a human. A person clicks Retry only
///              after confirming in eDrafter's dashboard.
/// </summary>
public sealed class Reconciler(
    AppDbContext db,
    EDrafterApiClient client,
    SpendLedger ledger,
    ZohoSigningService zohoSigning,
    ILogger<Reconciler> logger)
{
    public async Task<List<ReconcileResult>> ReconcileAllAsync(CancellationToken ct = default)
    {
        var unresolved = await ledger.GetUnresolvedAsync(ct);
        var results = new List<ReconcileResult>();

        foreach (var attempt in unresolved)
        {
            try
            {
                results.Add(attempt.Kind switch
                {
                    SpendKind.Order => await ReconcileOrderAsync(attempt, ct),
                    SpendKind.Esign => await ReconcileEsignAsync(attempt, ct),
                    SpendKind.ZohoEsign => await zohoSigning.ReconcileAsync(attempt, ct),
                    _ => new ReconcileResult(attempt.Id, attempt.Kind, false, "Unknown kind")
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciliation failed for attempt {Id}", attempt.Id);
                results.Add(new ReconcileResult(attempt.Id, attempt.Kind, false, $"Error: {ex.Message}"));
            }
        }

        return results;
    }

    private async Task<ReconcileResult> ReconcileOrderAsync(SpendAttempt attempt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(attempt.OurRefId))
            return new ReconcileResult(attempt.Id, attempt.Kind, false,
                "No refId recorded — cannot reconcile. Needs manual review in eDrafter's dashboard.");

        var found = await client.FindOrdersByRefAsync(attempt.OurRefId, ct);

        if (found is null or { Count: 0 })
        {
            logger.LogWarning(
                "Attempt {Id}: no order found for refId {RefId}. NOT retrying — flagged for review.",
                attempt.Id, attempt.OurRefId);

            return new ReconcileResult(attempt.Id, attempt.Kind, false,
                $"No order exists for refId {attempt.OurRefId}. The spend probably did not happen, " +
                "but this is NOT retried automatically — a human must confirm in eDrafter's dashboard first.");
        }

        var order = found.Orders[0];
        await ledger.AdoptAsync(attempt.Id, order.Idd.ToString(), ct);

        // Bring the agreement back in step with reality.
        if (attempt.AgreementId is { } agid)
        {
            var a = await db.Agreements.FirstOrDefaultAsync(x => x.Id == agid, ct);
            if (a is not null)
            {
                a.OrderIdd = order.Idd;
                a.OrderDisplayId = order.DisplayId;
                a.Status = AgreementStatus.OrderPlaced;
                a.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }

        return new ReconcileResult(attempt.Id, attempt.Kind, true,
            $"Order {order.Idd} exists for refId {attempt.OurRefId} — adopted. The money did move.");
    }

    private async Task<ReconcileResult> ReconcileEsignAsync(SpendAttempt attempt, CancellationToken ct)
    {
        if (attempt.AgreementId is not { } agid)
            return new ReconcileResult(attempt.Id, attempt.Kind, false, "No agreement linked");

        var a = await db.Agreements.Include(x => x.Signatories).FirstOrDefaultAsync(x => x.Id == agid, ct);
        if (a is null)
            return new ReconcileResult(attempt.Id, attempt.Kind, false, "Agreement not found");

        // GET /esign is newest-first; match on the document name we would have sent.
        var expectedName = $"Lease Agreement - {a.FirstPartyName} & {a.SecondPartyName}";
        var list = await client.ListEsignDocumentsAsync(ct);

        var match = list?.Documents.FirstOrDefault(d =>
            string.Equals(d.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
            d.CreatedAt >= attempt.CreatedAt.AddMinutes(-5));

        if (match is null)
        {
            logger.LogWarning(
                "Attempt {Id}: no e-sign document matching '{Name}'. NOT resending — flagged for review.",
                attempt.Id, expectedName);

            return new ReconcileResult(attempt.Id, attempt.Kind, false,
                $"No e-sign document matching '{expectedName}'. NOT resent automatically — " +
                "a resend would charge again, per signatory, with no refund. Human review required.");
        }

        await ledger.AdoptAsync(attempt.Id, match.DocumentId, ct);

        a.EsignDocumentId = match.DocumentId;
        a.Status = AgreementStatus.SentForSigning;
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ReconcileResult(attempt.Id, attempt.Kind, true,
            $"e-Sign document {match.DocumentId} found — adopted. The charge already happened.");
    }
}

public sealed record ReconcileResult(long AttemptId, SpendKind Kind, bool Resolved, string Message);
