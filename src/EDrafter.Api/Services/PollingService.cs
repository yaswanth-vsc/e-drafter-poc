using EDrafter.Api.Data;
using EDrafter.Api.EDrafterClient;
using EDrafter.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Services;

/// <summary>
/// The belt to the webhook's braces, and the only way to see several things at all.
///
/// eDrafter publishes exactly two events: order.completed and esign.completed. There is
/// NO webhook for order failure, e-sign expiry, e-sign decline, or individual signatures.
/// Partial progress — one of two parties has signed — is invisible except here.
///
/// It also covers a missed webhook delivery, which would otherwise leave an order sitting
/// in Pending forever.
/// </summary>
public sealed class PollingService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<PollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(config.GetValue("Polling:IntervalMinutes", 10));
        logger.LogInformation("Polling service started; interval {Interval}.", interval);

        // Let the app finish starting before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (Exception ex)
            {
                // A failed sweep must never kill the loop.
                logger.LogError(ex, "Polling sweep failed; will retry next interval.");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<EDrafterApiClient>();
        var documents = scope.ServiceProvider.GetRequiredService<DocumentService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<AgreementHub>>();

        await PollOrdersAsync(db, client, documents, hub, ct);
        await PollSigningAsync(db, client, documents, hub, ct);
    }

    /// <summary>Orders still waiting on a stamp — covers a missed order.completed.</summary>
    private async Task PollOrdersAsync(
        AppDbContext db, EDrafterApiClient client, DocumentService documents,
        IHubContext<AgreementHub> hub, CancellationToken ct)
    {
        var pending = await db.Agreements
            .Where(a => a.Status == AgreementStatus.OrderPlaced ||
                        a.Status == AgreementStatus.OrderProcessing ||
                        a.Status == AgreementStatus.OrderOnHold)
            .ToListAsync(ct);

        foreach (var a in pending)
        {
            if (a.OrderIdd is null) continue;

            // Check the order itself first. eDrafter's real status set is larger than
            // their documentation: Hold and rejection are states an order can sit in
            // indefinitely, and polling only for stamps would wait on them forever.
            var order = await client.GetOrderAsync(a.OrderIdd.Value, ct);
            if (order is not null)
            {
                if (!string.IsNullOrWhiteSpace(order.RejectionReason))
                {
                    a.Status = AgreementStatus.OrderRejected;
                    a.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);

                    logger.LogError(
                        "Order {OrderId} was REJECTED by eDrafter: {Reason}. Refund status: {Refund}.",
                        a.OrderIdd, order.RejectionReason, order.RefundStatus ?? "unknown");

                    await hub.Clients.All.SendAsync("AgreementUpdated", new
                    {
                        id = a.Id,
                        status = a.Status.ToString(),
                        message = $"eDrafter rejected this order: {order.RejectionReason}"
                    }, ct);
                    continue;
                }

                if (string.Equals(order.Status, EDrafterOrderStatus.Hold, StringComparison.OrdinalIgnoreCase))
                {
                    if (a.Status != AgreementStatus.OrderOnHold)
                    {
                        a.Status = AgreementStatus.OrderOnHold;
                        a.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);

                        logger.LogWarning(
                            "Order {OrderId} is ON HOLD at eDrafter: {Reason}. Will resume at {Before}.",
                            a.OrderIdd, order.HoldReason ?? "(no reason given)",
                            order.StatusBeforeHold ?? "(unknown)");

                        await hub.Clients.All.SendAsync("AgreementUpdated", new
                        {
                            id = a.Id,
                            status = a.Status.ToString(),
                            message = string.IsNullOrWhiteSpace(order.HoldReason)
                                ? "eDrafter has put this order on hold."
                                : $"On hold at eDrafter: {order.HoldReason}"
                        }, ct);
                    }
                    continue;
                }

                // Released from hold, or simply progressing.
                if (a.Status == AgreementStatus.OrderOnHold)
                {
                    a.Status = AgreementStatus.OrderProcessing;
                    a.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Order {OrderId} released from hold.", a.OrderIdd);
                }
            }

            var stamps = await client.GetOrderStampsAsync(a.OrderIdd.Value, ct);

            // Trust eDrafter's explicit readiness flag, not a status string. `ready`
            // is what their own dashboard means by "the e-stamp is ready to attach".
            if (stamps is null || !stamps.Ready || stamps.Stamps.Count == 0) continue;

            var stamp = stamps.Stamps[0];
            a.StampId = stamp.EffectiveStampId;
            a.CertificateNo = stamp.CertificateNo;

            var pdf = await client.DownloadStampAsync(a.OrderIdd.Value, stamp.EffectiveStampId, ct);
            a.StampPdfPath = documents.SaveStamp(a.Id, pdf);

            a.Status = AgreementStatus.StampReady;
            a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Polling found stamp {Cert} for agreement {Id} (webhook may have been missed).",
                stamp.CertificateNo, a.Id);

            await hub.Clients.All.SendAsync("AgreementUpdated", new
            {
                id = a.Id,
                status = a.Status.ToString(),
                certificateNo = a.CertificateNo,
                message = "Your e-stamp is ready."
            }, ct);
        }
    }

    /// <summary>
    /// Documents out for signature. This is the ONLY way to see a decline, an expiry,
    /// or one-of-two signed — none of those raise a webhook.
    /// </summary>
    private async Task PollSigningAsync(
        AppDbContext db, EDrafterApiClient client, DocumentService documents,
        IHubContext<AgreementHub> hub, CancellationToken ct)
    {
        var outForSigning = await db.Agreements
            .Include(a => a.Signatories)
            .Where(a => a.Status == AgreementStatus.SentForSigning ||
                        a.Status == AgreementStatus.PartiallySigned)
            .ToListAsync(ct);

        foreach (var a in outForSigning)
        {
            if (a.EsignDocumentId is null) continue;

            var status = await client.GetEsignStatusAsync(a.EsignDocumentId, ct);
            if (status is null) continue;

            var changed = false;
            foreach (var s in status.Signatories)
            {
                var sig = a.Signatories.FirstOrDefault(x =>
                    string.Equals(x.Email, s.Email, StringComparison.OrdinalIgnoreCase));
                if (sig is null || sig.Status == s.Status) continue;

                sig.Status = s.Status;
                sig.SignedAt = s.SignedAt;
                changed = true;
            }

            var previous = a.Status;

            a.Status = status.Status switch
            {
                "completed" => AgreementStatus.Signed,
                // No webhook exists for either of these. Polling is the only signal.
                "declined" => AgreementStatus.Failed,
                "expired" => AgreementStatus.Failed,
                _ when a.Signatories.Any(s => s.Status == "signed") => AgreementStatus.PartiallySigned,
                _ => a.Status
            };

            if (a.Status == AgreementStatus.Signed && a.SignedPdfPath is null)
            {
                var signed = await client.DownloadSignedAsync(a.EsignDocumentId, ct);
                a.SignedPdfPath = documents.SaveSigned(a.Id, signed);
                changed = true;
            }

            if (a.Status == AgreementStatus.Failed && previous != AgreementStatus.Failed)
            {
                // Worth stating plainly in the log: the money is gone either way.
                logger.LogWarning(
                    "Agreement {Id} ended as {Status} — the e-sign charge of ₹{Cost} is NOT refunded.",
                    a.Id, status.Status, (a.EsignCostPaise ?? 0) / 100m);
            }

            if (!changed && a.Status == previous) continue;

            a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await hub.Clients.All.SendAsync("AgreementUpdated", new
            {
                id = a.Id,
                status = a.Status.ToString(),
                signatories = a.Signatories.Select(s => new { s.Name, s.Status }),
                message = a.Status switch
                {
                    AgreementStatus.Signed => "All parties have signed.",
                    AgreementStatus.Failed => "The signing request ended without completion.",
                    AgreementStatus.PartiallySigned => "A signatory has signed; waiting on the rest.",
                    _ => "Status updated."
                }
            }, ct);
        }
    }
}
