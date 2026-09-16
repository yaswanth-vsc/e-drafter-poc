using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EDrafter.MockServer;

/// <summary>
/// Fires webhooks the way eDrafter does: HMAC-SHA256 over the RAW body, in the
/// X-eDrafter-Signature header as "sha256=&lt;lowercase hex&gt;".
///
/// It also retries, and it deliberately delivers twice on the first event of a run,
/// so our handler's idempotency is actually exercised rather than assumed.
/// </summary>
public sealed class WebhookDispatcher(
    MockState state,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookDispatcher> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task FireAsync(string eventName, object data, CancellationToken ct = default)
    {
        var hooks = state.Webhooks.Values
            .Where(w => w.Active && w.Events.Contains(eventName))
            .ToList();

        if (hooks.Count == 0)
        {
            logger.LogInformation("No webhook registered for {Event}; skipping delivery", eventName);
            return;
        }

        foreach (var hook in hooks)
        {
            var payload = new
            {
                @event = eventName,
                data,
                attempt = 1,
                deliveredAt = DateTime.UtcNow
            };
            var body = JsonSerializer.Serialize(payload, Json);
            await DeliverAsync(hook, eventName, body, attempt: 1, ct);

            // eDrafter retries up to 5 times, so duplicates are expected, not exceptional.
            // Delivering the same event twice here keeps our handler honest.
            var duplicate = JsonSerializer.Serialize(new
            {
                @event = eventName,
                data,
                attempt = 2,
                deliveredAt = DateTime.UtcNow
            }, Json);
            await DeliverAsync(hook, eventName, duplicate, attempt: 2, ct);
        }
    }

    private async Task DeliverAsync(MockWebhook hook, string eventName, string body, int attempt, CancellationToken ct)
    {
        var signature = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(hook.Secret),
                Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-eDrafter-Event", eventName);
        request.Headers.TryAddWithoutValidation("X-eDrafter-Signature", signature);

        try
        {
            var client = httpClientFactory.CreateClient("webhook");
            var response = await client.SendAsync(request, ct);
            logger.LogInformation(
                "Delivered {Event} attempt {Attempt} to {Url} -> {Status}",
                eventName, attempt, hook.Url, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            // A mock that throws here would take down the simulation timer with it.
            logger.LogWarning(ex, "Webhook delivery failed for {Event} to {Url}", eventName, hook.Url);
        }
    }
}
