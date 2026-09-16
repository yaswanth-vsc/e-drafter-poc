namespace EDrafter.Api.Services;

/// <summary>
/// Holds the webhook signing secret for the life of the process.
///
/// eDrafter shows the secret exactly ONCE, when the webhook is registered. In production
/// it belongs in Key Vault and comes in through configuration; this store exists so that
/// a local run — where the mock is restarted often and forgets its registrations — can
/// self-register at startup and still verify signatures.
///
/// Configuration always wins: if EDrafter:WebhookSecret is set, that is the secret and
/// nothing here overrides it.
/// </summary>
public sealed class WebhookSecretStore(IConfiguration config)
{
    private string? _runtimeSecret;

    /// <summary>
    /// The secret in use. Order: an explicitly configured value, then a secret file if one
    /// is pointed at, then whatever startup registration captured.
    ///
    /// The file exists for tunnelled local testing. Re-registering a webhook mints a NEW
    /// secret, and restarting to pick it up drops the tunnel, which forces another
    /// registration - a loop. Reading the file each time breaks that: re-register, write
    /// the file, and the running process picks it up without a restart.
    /// </summary>
    public string? Current
    {
        get
        {
            var configured = config["EDrafter:WebhookSecret"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            var path = config["EDrafter:WebhookSecretFile"];
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var fromFile = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrWhiteSpace(fromFile)) return fromFile;
                    }
                }
                catch (IOException)
                {
                    // Being rewritten as we read. Fall through rather than throw - the
                    // next delivery will retry, and eDrafter retries up to 5 times.
                }
            }

            return _runtimeSecret;
        }
    }

    public bool IsFromConfig => !string.IsNullOrWhiteSpace(config["EDrafter:WebhookSecret"]);

    public void SetRuntimeSecret(string secret) => _runtimeSecret = secret;
}
