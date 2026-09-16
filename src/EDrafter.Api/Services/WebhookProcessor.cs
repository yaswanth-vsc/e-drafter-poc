using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EDrafter.Api.Data;
using EDrafter.Api.EDrafterClient;
using EDrafter.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Services;

/// <summary>
/// Handles eDrafter's webhook deliveries.
///
/// Three things this must get right, all of them from the API's actual behaviour:
///
///  1. VERIFY OVER THE RAW BODY. JSON model binding normalises whitespace, which changes
///     the bytes and breaks the HMAC. The endpoint reads the raw stream.
///  2. FIXED-TIME COMPARISON. Never ==, which leaks timing information.
///  3. IDEMPOTENT. eDrafter retries up to 5 times, so the same event WILL arrive twice.
///     A second delivery must be a no-op — not a second PDF, and emphatically not a
///     second POST /esign, which would charge again per signatory with no refund.
/// </summary>
public sealed class WebhookProcessor(
    AppDbContext db,
    EDrafterApiClient client,
    DocumentService documents,
    IHubContext<AgreementHub> hub,
    WebhookSecretStore secrets,
    ILogger<WebhookProcessor> logger)
{
    public bool VerifySignature(string rawBody, string? receivedSignature)
    {
        var secret = secrets.Current;
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogWarning("No webhook secret known — signature check skipped (dev only).");
            return true;
        }

        if (string.IsNullOrWhiteSpace(receivedSignature)) return false;

        var expected = "sha256=" + Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(receivedSignature),
            Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// Returns true when the event was newly processed, false when it was a duplicate.
    /// The caller returns 200 either way — a duplicate is a success, not an error.
    /// </summary>
    public async Task<bool> ProcessAsync(string rawBody, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        if (payload?.Event is null)
        {
            logger.LogWarning("Webhook payload had no event name; ignoring.");
            return false;
        }

        // The dedupe key: event name + the subject it concerns. Attempt number is
        // deliberately excluded, so eDrafter's retries collide with the first delivery.
        //
        // The subject alone is NOT enough. eDrafter's order ids are only unique within
        // their system, and a mock restart reissues the same ids from the start — so a
        // genuinely new order can collide with a row left by a previous run and be
        // silently swallowed as a "duplicate". Binding the key to OUR agreement id keeps
        // retries colliding while letting a reused id through.
        var subject = payload.Data?.OrderId?.ToString()
                      ?? payload.Data?.DocumentId
                      ?? "unknown";

        var scopeId = await ResolveAgreementIdAsync(payload, ct);
        var key = scopeId is null
            ? $"{payload.Event}:{subject}"
            : $"{payload.Event}:{subject}:{scopeId}";

        var already = await db.WebhookEvents.FindAsync([key], ct);
        if (already is not null)
        {
            logger.LogInformation(
                "Duplicate webhook {Key} (attempt {Attempt}) — no-op, as designed.",
                key, payload.Attempt);
            return false;
        }

        db.WebhookEvents.Add(new WebhookEvent
        {
            EventKey = key,
            EventName = payload.Event,
            Payload = rawBody
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two deliveries raced. The primary key caught it; this one is the duplicate.
            logger.LogInformation("Concurrent duplicate webhook {Key} rejected by primary key.", key);
            return false;
        }

        try
        {
            switch (payload.Event)
            {
                case "order.completed":
                    await HandleOrderCompletedAsync(payload, ct);
                    break;
                case "esign.completed":
                    await HandleEsignCompletedAsync(payload, ct);
                    break;
                default:
                    logger.LogInformation("No handler for event {Event}; recorded only.", payload.Event);
                    break;
            }

            var evt = await db.WebhookEvents.FindAsync([key], ct);
            if (evt is not null)
            {
                evt.Processed = true;
                evt.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook {Key} recorded but handling failed.", key);
        }

        return true;
    }

    /// <summary>
    /// Finds the agreement this event concerns, so the dedupe key can be scoped to it.
    /// Returns null when we do not track the subject, in which case the key falls back to
    /// the event + subject alone.
    /// </summary>
    private async Task<Guid?> ResolveAgreementIdAsync(WebhookPayload payload, CancellationToken ct)
    {
        if (payload.Data?.OrderId is { } orderId)
        {
            // Same ordering as the handler, so the dedupe key names the agreement that
            // will actually be updated.
            return await db.Agreements
                .Where(a => a.OrderIdd == orderId)
                .OrderBy(a => a.Status == AgreementStatus.StampReady ? 1 : 0)
                .ThenByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (payload.Data?.DocumentId is { Length: > 0 } documentId)
        {
            return await db.Agreements
                .Where(a => a.EsignDocumentId == documentId)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(ct);
        }

        return null;
    }

    private async Task HandleOrderCompletedAsync(WebhookPayload payload, CancellationToken ct)
    {
        var orderId = payload.Data?.OrderId;
        if (orderId is null) return;

        // Prefer an agreement still waiting on its stamp, newest first. Ordinarily there
        // is exactly one match, since eDrafter order ids are unique. This ordering only
        // matters against a restarted mock, which reissues ids a previous run already used.
        var agreement = await db.Agreements
            .Include(a => a.Signatories)
            .Where(a => a.OrderIdd == orderId)
            .OrderBy(a => a.Status == AgreementStatus.StampReady ? 1 : 0)
            .ThenByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (agreement is null)
        {
            logger.LogWarning("order.completed for order {OrderId}, which we do not track.", orderId);
            return;
        }

        var stamps = await client.GetOrderStampsAsync(orderId.Value, ct);

        // `ready` is eDrafter's own "the e-stamp is ready to attach" flag. Trust it over
        // any status string — their status set is larger than their documentation.
        if (stamps is { Ready: true, Stamps.Count: > 0 })
        {
            var stamp = stamps.Stamps[0];
            agreement.StampId = stamp.EffectiveStampId;
            agreement.CertificateNo = stamp.CertificateNo;

            // Downloading marks the stamp Used at eDrafter, so archive it now.
            var pdf = await client.DownloadStampAsync(orderId.Value, stamp.EffectiveStampId, ct);
            agreement.StampPdfPath = documents.SaveStamp(agreement.Id, pdf);
        }
        else
        {
            logger.LogWarning(
                "order.completed for {OrderId} but stamps are not ready yet (ready={Ready}). " +
                "Polling will pick it up.", orderId, stamps?.Ready);
        }

        agreement.Status = AgreementStatus.StampReady;
        agreement.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Order {OrderId} completed; stamp ready for agreement {Id}.",
            orderId, agreement.Id);

        await hub.Clients.All.SendAsync("AgreementUpdated", new
        {
            id = agreement.Id,
            status = agreement.Status.ToString(),
            certificateNo = agreement.CertificateNo,
            message = "Your e-stamp is ready."
        }, ct);
    }

    private async Task HandleEsignCompletedAsync(WebhookPayload payload, CancellationToken ct)
    {
        var documentId = payload.Data?.DocumentId;
        if (documentId is null) return;

        var agreement = await db.Agreements
            .Include(a => a.Signatories)
            .FirstOrDefaultAsync(a => a.EsignDocumentId == documentId, ct);

        if (agreement is null)
        {
            logger.LogWarning("esign.completed for document {Doc}, which we do not track.", documentId);
            return;
        }

        var status = await client.GetEsignStatusAsync(documentId, ct);
        if (status is not null)
        {
            foreach (var s in status.Signatories)
            {
                var sig = agreement.Signatories.FirstOrDefault(x =>
                    string.Equals(x.Email, s.Email, StringComparison.OrdinalIgnoreCase));
                if (sig is null) continue;
                sig.Status = s.Status;
                sig.SignedAt = s.SignedAt;
            }
        }

        var signed = await client.DownloadSignedAsync(documentId, ct);
        agreement.SignedPdfPath = documents.SaveSigned(agreement.Id, signed);
        agreement.Status = AgreementStatus.Signed;
        agreement.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("e-Sign {Doc} completed; signed PDF archived for agreement {Id}.",
            documentId, agreement.Id);

        await hub.Clients.All.SendAsync("AgreementUpdated", new
        {
            id = agreement.Id,
            status = agreement.Status.ToString(),
            message = "All parties have signed. The completed document is ready."
        }, ct);
    }
}

public sealed class WebhookPayload
{
    public string? Event { get; set; }
    public WebhookData? Data { get; set; }
    public int Attempt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public sealed class WebhookData
{
    public int? OrderId { get; set; }
    public string? DocumentId { get; set; }
    public string? Status { get; set; }
    public int? StampCount { get; set; }
    public string? SignedUrl { get; set; }
}
