namespace EDrafter.Api.Zoho;

/// <summary>
/// The "Zoho" config section. Secrets (ClientSecret, RefreshToken, WebhookSecret) belong in
/// the gitignored appsettings.Local.json — never in appsettings.json, which is committed.
/// </summary>
public sealed class ZohoOptions
{
    public const string Section = "Zoho";

    /// <summary>MUST be the India data centre — Aadhaar eSign exists only there.</summary>
    public string BaseUrl { get; set; } = "https://sign.zoho.in/api/v1/";

    /// <summary>OAuth accounts domain. Must match the data centre.</summary>
    public string AccountsUrl { get; set; } = "https://accounts.zoho.in";

    /// <summary>"RefreshToken" (unattended) or "DevToken" (the 1-hour token, for a quick try).</summary>
    public string AuthMode { get; set; } = ZohoAuthModes.RefreshToken;

    public string DevToken { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Permanent until revoked. Minted with ZohoSign.documents.ALL and ZohoSign.account.READ
    /// (the Zoho POC's mint-refresh-token.ps1). Scopes are frozen into it.
    /// </summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>Full "whsec_…" string from Zoho Sign → Settings → Developer → Webhooks.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Lets Zoho's unsigned "Test Url" button through. Development only.</summary>
    public bool WebhookAllowUnsigned { get; set; }

    /// <summary>
    /// The spending switch for Zoho. POST /requests/{id}/submit consumes credits and emails
    /// the signers; it refuses to run until this is true. Creating the draft and placing
    /// the signature boxes are free and run regardless, so the layout can be checked in
    /// the Zoho UI first.
    /// </summary>
    public bool ArmSpending { get; set; }

    /// <summary>
    /// How the signing link reaches each signer.
    ///   EMAIL      — email only.
    ///   EMAIL_SMS  — email AND SMS (0.5 credit per SMS per signer).
    /// SMS alone is NOT possible: Zoho rejects delivery_mode "SMS" with code 9013, and its
    /// help states email delivery cannot be disabled. Verified against the live API
    /// 2026-10-01 for "SMS", "SMS_ONLY", "SMSONLY" and "sms" — only EMAIL_SMS was accepted.
    /// </summary>
    public string DeliveryMode { get; set; } = ZohoDeliveryModes.EmailSms;

    /// <summary>
    /// HOW the signer signs:
    ///   AADHAAR — Aadhaar eSign (2 credits per signing), locked via allowed_cloud_provider_ids.
    ///   ZOHO    — an ordinary drawn/typed Zoho signature, no Aadhaar charge.
    /// </summary>
    public string SignMethod { get; set; } = ZohoSignMethods.Aadhaar;

    /// <summary>
    /// An OPTIONAL check before the document will open — not the signature itself:
    ///   NONE | EMAIL | SMS. SMS costs 0.5 credit per OTP per signer.
    /// Independent of <see cref="SignMethod"/>: Aadhaar signing with an SMS unlock is valid.
    /// </summary>
    public string RecipientAuth { get; set; } = ZohoRecipientAuth.None;

    /// <summary>ISO country code for recipient phone numbers.</summary>
    public string PhoneCountryIso { get; set; } = "IN";

    public bool DeliversBySms =>
        string.Equals(DeliveryMode, ZohoDeliveryModes.EmailSms, StringComparison.OrdinalIgnoreCase);

    public bool UsesAadhaar =>
        string.Equals(SignMethod, ZohoSignMethods.Aadhaar, StringComparison.OrdinalIgnoreCase);

    public bool AuthBySms =>
        string.Equals(RecipientAuth, ZohoRecipientAuth.Sms, StringComparison.OrdinalIgnoreCase);

    public bool AuthEnabled =>
        !string.Equals(RecipientAuth, ZohoRecipientAuth.None, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a phone number is required for every signer.</summary>
    public bool NeedsPhone => DeliversBySms || AuthBySms;

    /// <summary>
    /// Signing provider ids a signer may choose from. 25 = Aadhaar eSign. Empty means
    /// Aadhaar only — see <see cref="EffectiveCloudProviderIds"/>.
    /// </summary>
    public int[] AllowedCloudProviderIds { get; set; } = [];

    /// <summary>
    /// Aadhaar only unless configured otherwise. Resolved here rather than defaulted on the
    /// property, because the config binder appends to a pre-filled collection instead of
    /// replacing it.
    /// </summary>
    public int[] EffectiveCloudProviderIds =>
        AllowedCloudProviderIds is { Length: > 0 } ? AllowedCloudProviderIds : [ZohoCloudProviders.AadhaarEsign];

    public int ExpiryDays { get; set; } = 7;
    public bool EmailReminders { get; set; } = true;
    public int ReminderPeriodDays { get; set; } = 2;

    /// <summary>
    /// For our own spend ledger only — Zoho bills from its own meter regardless. 5 for the
    /// API send + 2 × 2 for Aadhaar eSign. Aadhaar is charged even in test mode (seen
    /// 2026-09-26); testing=true waives only the 5.
    /// </summary>
    public int CreditsPerAgreement { get; set; } = 9;

    /// <summary>Price of one Zoho credit on the India account: ₹6.</summary>
    public long CreditPricePaise { get; set; } = 600;

    /// <summary>
    /// "flat": actions[].fields is one array of field objects.
    /// "grouped": signatures go under actions[].fields.image_fields.
    /// Zoho's reference shows both shapes; the free draft step confirms which it takes.
    /// </summary>
    public string FieldsShape { get; set; } = "flat";

    /// <summary>
    /// "absolute": x_coord/y_coord/abs_width/abs_height in PDF points, top-left origin.
    /// "percent":  x_value/y_value/width/height as 0–100 of the page.
    /// If boxes land in the wrong place on the draft, switch this — nothing is charged.
    /// </summary>
    public string CoordinateMode { get; set; } = "absolute";

    /// <summary>Adds testing=true to submit. Watermarked, no legal value.</summary>
    public bool TestMode { get; set; }

    public int TimeoutSeconds { get; set; } = 60;
    public bool LogPayloads { get; set; } = true;
    public int LogMaxBodyChars { get; set; } = 4000;

    public bool HasCredentials => AuthMode == ZohoAuthModes.DevToken
        ? !string.IsNullOrWhiteSpace(DevToken)
        : !string.IsNullOrWhiteSpace(ClientId) &&
          !string.IsNullOrWhiteSpace(ClientSecret) &&
          !string.IsNullOrWhiteSpace(RefreshToken);
}

/// <summary>Values for <see cref="ZohoOptions.DeliveryMode"/> — Zoho's own strings.</summary>
public static class ZohoDeliveryModes
{
    public const string Email = "EMAIL";
    public const string EmailSms = "EMAIL_SMS";
}

/// <summary>Values for <see cref="ZohoOptions.SignMethod"/>.</summary>
public static class ZohoSignMethods
{
    public const string Aadhaar = "AADHAAR";
    public const string Zoho = "ZOHO";
}

/// <summary>Values for <see cref="ZohoOptions.RecipientAuth"/> — Zoho's verification_type.</summary>
public static class ZohoRecipientAuth
{
    public const string None = "NONE";
    public const string Email = "EMAIL";
    public const string Sms = "SMS";
}

public static class ZohoAuthModes
{
    public const string DevToken = "DevToken";
    public const string RefreshToken = "RefreshToken";
}

/// <summary>Zoho's ids for signing providers, from its cloud-signing API guide.</summary>
public static class ZohoCloudProviders
{
    public const int ZohoSign = 10;
    public const int EmudhraEkyc = 20;
    public const int AadhaarEsign = 25;
}
