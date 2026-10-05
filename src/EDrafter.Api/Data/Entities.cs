namespace EDrafter.Api.Data;

/// <summary>
/// The safety table. Every call that can move money writes a row here BEFORE the call,
/// and the row — not the HTTP response — is the source of truth about whether we spent.
///
/// The UNIQUE constraint on IdempotencyKey is the whole mechanism: a duplicate insert
/// fails before the HTTP call is ever made, so a double-click or a retried request
/// cannot produce a second charge.
/// </summary>
public sealed class SpendAttempt
{
    public long Id { get; set; }

    /// <summary>UNIQUE. Derived from the operation, not random — see SpendLedger.</summary>
    public string IdempotencyKey { get; set; } = "";

    public SpendKind Kind { get; set; }
    public SpendStatus Status { get; set; }

    /// <summary>Stored in paise to avoid any float/decimal drift in money.</summary>
    public long AmountPaise { get; set; }

    /// <summary>Our own reference, sent to eDrafter. The recovery handle after a timeout.</summary>
    public string? OurRefId { get; set; }

    /// <summary>eDrafter's id once known: order _idd, or the e-sign documentId.</summary>
    public string? EdrafterId { get; set; }

    public string? FailureReason { get; set; }
    public Guid? AgreementId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

public enum SpendKind
{
    Order = 0,
    Esign = 1,

    /// <summary>Zoho Sign POST /requests/{id}/submit — consumes Zoho credits.</summary>
    ZohoEsign = 2
}

public enum SpendStatus
{
    /// <summary>Row written, call not yet returned. A crash here leaves this behind — treat as UNKNOWN.</summary>
    Attempting = 0,
    Succeeded = 1,
    /// <summary>4xx — eDrafter rejected it, no money moved. Safe to fix and try again.</summary>
    FailedSafe = 2,
    /// <summary>Timeout or 5xx. Money may or may not have moved. NEVER auto-retry; reconcile.</summary>
    Unknown = 3,
    /// <summary>An Unknown that reconciliation resolved to "it did happen".</summary>
    Adopted = 4,
    /// <summary>An Unknown a human reviewed and confirmed did NOT happen.</summary>
    ConfirmedNotPlaced = 5
}

/// <summary>
/// Our own record of an agreement. We own this state machine; eDrafter is a downstream
/// dependency, not the source of truth.
/// No Aadhaar field anywhere — dropped by decision, see docs/15-POC-SCOPE-DECISIONS.md.
/// </summary>
public sealed class Agreement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FirstPartyName { get; set; } = "";
    public string FirstPartyEmail { get; set; } = "";
    public string FirstPartyPhone { get; set; } = "";

    public string SecondPartyName { get; set; } = "";
    public string SecondPartyEmail { get; set; } = "";
    public string SecondPartyPhone { get; set; } = "";

    // Lease specifics for Karnataka article 30(1)(i)
    public string PropertyAddress { get; set; } = "";
    public decimal ConsiderationAmount { get; set; }
    public decimal MonthlyRent { get; set; }
    public int LeaseTermMonths { get; set; }
    public DateOnly LeaseStartDate { get; set; }
    public string Purpose { get; set; } = "Rental Agreement";

    /// <summary>Computed duty: 0.5% of consideration, capped per article config.</summary>
    public decimal Denomination { get; set; }

    public AgreementStatus Status { get; set; } = AgreementStatus.Draft;

    /// <summary>Our reference, sent as refId. How we recover an order after a timeout.</summary>
    public string RefId { get; set; } = "";

    public int? OrderIdd { get; set; }
    public string? OrderDisplayId { get; set; }
    public int? StampId { get; set; }
    public string? CertificateNo { get; set; }

    public string? EsignDocumentId { get; set; }
    public string? SignMethod { get; set; }
    public long? EsignCostPaise { get; set; }

    public string? AgreementPdfPath { get; set; }
    public string? StampPdfPath { get; set; }
    public string? SignedPdfPath { get; set; }

    // ---- Zoho Sign ----------------------------------------------------------
    //
    // All nullable so SchemaUpgrader can add them to an existing database with a plain
    // ALTER TABLE, without having to pick a default for rows that predate them.

    /// <summary>"zoho" or "edrafter". Null on rows created before signing moved to Zoho.</summary>
    public string? SigningProvider { get; set; }

    /// <summary>The stamp paper followed by the agreement — the file uploaded to Zoho.</summary>
    public string? FinalPdfPath { get; set; }

    /// <summary>Set when the free Zoho draft is created, BEFORE anything is charged.</summary>
    public string? ZohoRequestId { get; set; }
    public string? ZohoDocumentId { get; set; }

    /// <summary>Signature fields are added once; a second PUT would duplicate every box.</summary>
    public DateTime? ZohoFieldsPlacedAt { get; set; }

    /// <summary>Set only after /submit succeeded — the point credits were consumed.</summary>
    public DateTime? ZohoSubmittedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<Signatory> Signatories { get; set; } = new();
}

public enum AgreementStatus
{
    Draft = 0,
    OrderPlaced = 1,
    OrderProcessing = 2,
    StampReady = 3,
    Preparing = 4,
    SentForSigning = 5,
    PartiallySigned = 6,
    Signed = 7,
    Failed = 8,
    NeedsReview = 9,
    Cancelled = 10,

    /// <summary>
    /// eDrafter has paused the order. Their dashboard exposes a Hold state with a
    /// holdReason and a statusBeforeHold, none of it in their API documentation.
    /// The money is already spent, so this is a wait, not a failure.
    /// </summary>
    OrderOnHold = 11,

    /// <summary>
    /// eDrafter rejected the order outright (rejectionReason is set). Undocumented
    /// path; whether the wallet is refunded is unconfirmed — question L5.
    /// </summary>
    OrderRejected = 12
}

public sealed class Signatory
{
    public long Id { get; set; }
    public Guid AgreementId { get; set; }
    public Agreement? Agreement { get; set; }

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? SignUrl { get; set; }
    public DateTime? SignedAt { get; set; }

    /// <summary>"first" or "second" party. Decides the signature corner and sign order.</summary>
    public string? Role { get; set; }

    /// <summary>Zoho signing_order: the second party is 1, the first party is 2.</summary>
    public int? SigningOrder { get; set; }

    /// <summary>Minted by Zoho when the draft is created; needed for fields and submit.</summary>
    public string? ZohoActionId { get; set; }
}

/// <summary>
/// Webhook dedupe. eDrafter retries up to 5 times, so the same event WILL arrive twice.
/// A second delivery must be a no-op, not a second PDF and not a second POST /esign.
/// </summary>
public sealed class WebhookEvent
{
    /// <summary>Composite of event name + subject id — the dedupe key.</summary>
    public string EventKey { get; set; } = "";
    public string EventName { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public bool Processed { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
