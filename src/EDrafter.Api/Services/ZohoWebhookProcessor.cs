using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EDrafter.Api.Data;
using EDrafter.Api.Zoho;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EDrafter.Api.Services;

/// <summary>
/// Receives Zoho Sign events. Registered in the Zoho web UI only (Settings → Developer →
/// Webhooks); there is no API for it.
///
/// Verification is Zoho's scheme, ported from the Zoho POC: HMAC-SHA256 over the RAW body,
/// BASE64-encoded (not hex), keyed with the whole "whsec_…" secret, in X-ZS-WEBHOOK-SIGNATURE.
///
/// The endpoint must always answer 200 once a delivery is authenticated — Zoho disables a
/// webhook after repeated failures, and a 500 from our own bug would switch off the only
/// push signal the flow has. Polling is the fallback either way.
/// </summary>
public sealed class ZohoWebhookProcessor(
    AppDbContext db,
    ZohoSigningService signing,
    IOptions<ZohoOptions> options,
    ILogger<ZohoWebhookProcessor> logger)
{
    public const string SignatureHeader = "X-ZS-WEBHOOK-SIGNATURE";
    public const string TimestampHeader = "X-ZS-WEBHOOK-TIMESTAMP";

    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly ZohoOptions _o = options.Value;

    public bool Verify(string rawBody, string? signature, string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(_o.WebhookSecret))
        {
            logger.LogWarning("Zoho:WebhookSecret is not set — accepting the Zoho webhook UNVERIFIED. Local use only.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(signature))
        {
            if (_o.WebhookAllowUnsigned)
            {
                logger.LogWarning("Unsigned Zoho webhook accepted (Zoho:WebhookAllowUnsigned) — Zoho's Test Url button.");
                return true;
            }

            logger.LogWarning("Zoho webhook has no {Header}; rejected. If this was Zoho's Test Url button, " +
                              "set Zoho:WebhookAllowUnsigned to true.", SignatureHeader);
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_o.WebhookSecret));
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(signature.Trim())))
        {
            logger.LogWarning("Zoho webhook rejected: signature mismatch.");
            return false;
        }

        // Optional replay guard: only enforced when Zoho sends the timestamp header.
        if (!string.IsNullOrWhiteSpace(timestamp) && long.TryParse(timestamp.Trim(), out var epoch))
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (age.Duration() > MaxClockSkew)
            {
                logger.LogWarning("Zoho webhook rejected: timestamp is {Age} old.", age.Duration());
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns false for a duplicate or an event we do not track.</summary>
    public async Task<bool> ProcessAsync(string rawBody, CancellationToken ct = default)
    {
        var node = JsonNode.Parse(rawBody);
        var requests = node?["requests"];
        var notifications = node?["notifications"];

        var requestId = requests?["request_id"]?.ToString();
        var operation = notifications?["operation_type"]?.ToString() ?? "unknown";
        var performedBy = notifications?["performed_by_email"]?.ToString() ?? "";
        var performedAt = notifications?["performed_at"]?.ToString() ?? "";

        logger.LogInformation("Zoho webhook: operation={Op} request={Request} by={By} status={Status}",
            operation, requestId, performedBy, requests?["request_status"]?.ToString());

        if (requests is null || string.IsNullOrWhiteSpace(requestId))
        {
            logger.LogWarning("Zoho webhook without a request_id; recorded nothing.");
            return false;
        }

        // Retries of one event share operation, signer and time; different events do not.
        var key = $"zoho:{operation}:{requestId}:{performedBy}:{performedAt}";
        if (key.Length > 200) key = key[..200];

        if (await db.WebhookEvents.FindAsync([key], ct) is not null)
        {
            logger.LogInformation("Duplicate Zoho webhook {Key} — no-op.", key);
            return false;
        }

        db.WebhookEvents.Add(new WebhookEvent { EventKey = key, EventName = $"zoho.{operation}", Payload = rawBody });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            logger.LogInformation("Concurrent duplicate Zoho webhook {Key} rejected by primary key.", key);
            return false;
        }

        var agreement = await db.Agreements
            .Include(a => a.Signatories)
            .FirstOrDefaultAsync(a => a.ZohoRequestId == requestId, ct);

        if (agreement is null)
        {
            // The POC account's webhook may still carry events for the old Zoho POC's requests.
            logger.LogWarning("Zoho webhook for request {Request}, which we do not track.", requestId);
            return false;
        }

        await signing.ApplyAsync(agreement, ZohoSignClient.ReadState(requests), operation, ct);

        var evt = await db.WebhookEvents.FindAsync([key], ct);
        if (evt is not null)
        {
            evt.Processed = true;
            evt.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}
