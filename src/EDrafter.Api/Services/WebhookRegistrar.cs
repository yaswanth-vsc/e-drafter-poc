using EDrafter.Api.EDrafterClient;

namespace EDrafter.Api.Services;

/// <summary>
/// Registers our webhook endpoint with eDrafter at startup, so a local run works without
/// a manual curl and a copied secret.
///
/// Why this is needed: the mock keeps its registrations in memory, so restarting it drops
/// them and the stamp then never arrives by webhook — the polling fallback would catch it,
/// but only on its 10-minute sweep, which is far too slow to demo.
///
/// Deliberately limited:
///   - Mock mode only by default. Against the LIVE API, registering a webhook pointed at
///     localhost would be wrong, and the secret belongs in Key Vault rather than in a
///     field on a running process. Set Webhooks:AutoRegister to override.
///   - A configured EDrafter:WebhookSecret always wins; this never overrides it.
///   - Failure is logged and shrugged off. Polling still covers everything, so a webhook
///     that cannot be registered must not stop the API from starting.
/// </summary>
public sealed class WebhookRegistrar(
    IServiceScopeFactory scopeFactory,
    WebhookSecretStore secrets,
    IConfiguration config,
    ILogger<WebhookRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var isLive = string.Equals(config["EDrafter:Mode"], "Live", StringComparison.OrdinalIgnoreCase);
        var autoRegister = config.GetValue("Webhooks:AutoRegister", !isLive);

        if (!autoRegister)
        {
            logger.LogInformation(
                "Webhook auto-registration is off ({Mode} mode). Register manually and set " +
                "EDrafter:WebhookSecret.", isLive ? "Live" : "Mock");
            return;
        }

        var callbackUrl = config["Webhooks:CallbackUrl"] ?? "http://localhost:5100/webhooks/edrafter";

        try
        {
            using var scope = scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<EDrafterApiClient>();

            var existing = await client.ListWebhooksAsync(ct);
            var already = existing.FirstOrDefault(w =>
                string.Equals(w.Url, callbackUrl, StringComparison.OrdinalIgnoreCase) && w.Active);

            if (already is not null && secrets.Current is not null)
            {
                logger.LogInformation("Webhook already registered for {Url}; secret already known.", callbackUrl);
                return;
            }

            // Re-register when the secret is unknown: eDrafter shows it only at creation,
            // so an existing hook we have no secret for is useless to us.
            var result = await client.RegisterWebhookAsync(
                callbackUrl, ["order.completed", "order.cancelled", "esign.completed"], ct);

            if (result?.Webhook?.Secret is { Length: > 0 } secret)
            {
                if (secrets.IsFromConfig)
                {
                    logger.LogInformation(
                        "Registered webhook for {Url}, but keeping the configured secret.", callbackUrl);
                }
                else
                {
                    secrets.SetRuntimeSecret(secret);
                    logger.LogInformation(
                        "Registered webhook for {Url} and captured its signing secret.", callbackUrl);
                }
            }
            else
            {
                logger.LogWarning("Webhook registration returned no secret; signatures cannot be verified.");
            }
        }
        catch (Exception ex)
        {
            // Never block startup for this. Polling covers every state a webhook would.
            logger.LogWarning(ex,
                "Could not auto-register the webhook. Falling back to polling, which is slower " +
                "but complete.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
