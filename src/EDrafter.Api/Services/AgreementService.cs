using EDrafter.Api.Data;
using EDrafter.Api.EDrafterClient;
using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Services;

/// <summary>
/// Orchestrates the agreement lifecycle. Both spending calls go through the ledger;
/// nothing here calls eDrafter's POST endpoints directly.
/// </summary>
public sealed class AgreementService(
    AppDbContext db,
    EDrafterApiClient client,
    SpendLedger ledger,
    DutyCalculator duty,
    EsignPricing esignPricing,
    SignaturePlacementInspector placementInspector,
    IConfiguration config,
    ILogger<AgreementService> logger)
{
    private string ScopeState => config["Scope:State"] ?? "Karnataka";
    private string ScopeArticle => config["Scope:ArticleCode"] ?? "30(1)(i)";
    private int ScopeQuantity => config.GetValue("Scope:Quantity", 1);

    /// <summary>
    /// Which parties are sent for signature: "both", "first" or "second".
    /// Each signatory is a separate charge, so this is a cost lever as much as a
    /// workflow choice.
    /// </summary>
    private string SignatoryScope =>
        SigningProvider == ZohoSigningService.Provider
            ? "both"   // Zoho: both parties always sign, second party first
            : (config["Esign:Signatories"] ?? "second").Trim().ToLowerInvariant();

    /// <summary>"zoho" (default) or "edrafter" — who collects the signatures.</summary>
    private string SigningProvider =>
        (config["Signing:Provider"] ?? ZohoSigningService.Provider).Trim().ToLowerInvariant();

    // ---- Draft ------------------------------------------------------------

    public async Task<Agreement> CreateDraftAsync(CreateAgreementInput input, CancellationToken ct = default)
    {
        // eDrafter prices the duty, not us — their figure is what the order will be
        // charged, and a local percentage that drifts from theirs would quote one price
        // and bill another. A refusal here is a validation message for the user.
        var d = await duty.CalculateAsync(ScopeArticle, input.ConsiderationAmount, ct);

        var agreement = new Agreement
        {
            FirstPartyName = input.FirstPartyName.Trim(),
            FirstPartyEmail = input.FirstPartyEmail.Trim(),
            FirstPartyPhone = input.FirstPartyPhone.Trim(),
            SecondPartyName = input.SecondPartyName.Trim(),
            SecondPartyEmail = input.SecondPartyEmail.Trim(),
            SecondPartyPhone = input.SecondPartyPhone.Trim(),
            PropertyAddress = input.PropertyAddress.Trim(),
            ConsiderationAmount = input.ConsiderationAmount,
            MonthlyRent = input.MonthlyRent,
            LeaseTermMonths = input.LeaseTermMonths,
            LeaseStartDate = input.LeaseStartDate,
            Purpose = "Lease Agreement",
            Denomination = d.Duty,
            Status = AgreementStatus.Draft,
            SigningProvider = SigningProvider,
            RefId = $"EDR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
        };

        // Who actually signs. e-Sign is charged PER SIGNATORY at link generation and is
        // never refunded, so this directly halves or doubles the cost of every document.
        // Configurable rather than hardcoded precisely because it is a money decision.
        if (SignatoryScope is "both" or "first")
        {
            agreement.Signatories.Add(new Signatory
            {
                Name = agreement.FirstPartyName,
                Email = agreement.FirstPartyEmail,
                Phone = agreement.FirstPartyPhone,
                Role = SignatureLayout.FirstParty,
                SigningOrder = 2
            });
        }

        if (SignatoryScope is "both" or "second")
        {
            agreement.Signatories.Add(new Signatory
            {
                Name = agreement.SecondPartyName,
                Email = agreement.SecondPartyEmail,
                Phone = agreement.SecondPartyPhone,
                Role = SignatureLayout.SecondParty,
                SigningOrder = 1
            });
        }

        if (agreement.Signatories.Count == 0)
        {
            throw new InvalidOperationException(
                $"Esign:Signatories is '{SignatoryScope}'. Expected 'both', 'first' or 'second' — " +
                "a document with no signatories cannot be sent.");
        }

        db.Agreements.Add(agreement);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Draft {Id} created. refId={RefId} duty=₹{Duty} (from eDrafter: {FromEDrafter})",
            agreement.Id, agreement.RefId, d.Duty, d.FromEDrafter);

        return agreement;
    }

    // ---- Quote (free) -----------------------------------------------------

    public async Task<QuotePreview> GetQuoteAsync(Guid agreementId, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);
        var productId = await ResolveProductIdAsync(ct);

        var quote = await client.GetQuoteAsync(new QuoteRequest
        {
            ProductId = productId,
            Quantity = ScopeQuantity,
            Denomination = a.Denomination,
            DoorstepDelivery = false   // always: saves ₹99 + GST, the biggest single lever
        }, ct);

        // Zoho signing is not in the quote yet — the quote is eDrafter's breakdown alone.
        var zoho = (a.SigningProvider ?? SigningProvider) == ZohoSigningService.Provider;
        var signMethod = config["Esign:DefaultSignMethod"] ?? "phone_otp";
        var esign = zoho ? null : esignPricing.Compute(signMethod, a.Signatories.Count);
        var d = await duty.CalculateAsync(ScopeArticle, a.ConsiderationAmount, ct);

        return new QuotePreview(
            Duty: d,
            StampValue: quote?.StampValue ?? 0,
            ServiceCharge: quote?.ServiceCharge ?? 0,
            Shipping: quote?.Shipping ?? 0,
            Gst: quote?.Gst ?? 0,
            OrderTotal: quote?.Total ?? 0,
            WalletBalance: quote?.WalletBalance ?? 0,
            Affordable: quote?.Affordable ?? false,
            Esign: esign,
            GrandTotal: (quote?.Total ?? 0) + (esign?.TotalRupees ?? 0),
            SigningProvider: zoho ? ZohoSigningService.Provider : "edrafter");
    }

    // ---- Place order (SPENDS) ---------------------------------------------

    public async Task<SpendOutcome<CreateOrderResponse>> PlaceOrderAsync(
        Guid agreementId, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);

        if (a.Status != AgreementStatus.Draft)
            return SpendOutcome<CreateOrderResponse>.Blocked(
                $"Agreement is {a.Status}, not Draft. Refusing to order again.");

        var productId = await ResolveProductIdAsync(ct);

        // The same choice the duty calculation made: the article's rule says which amount
        // field it prices on. 30(1)(i) wants rentalSecurity and rejects an order that
        // carries only considerationPrice. Free read.
        var rule = await duty.GetRuleAsync(ScopeArticle, ct);
        var wantsRental = string.Equals(rule?.Requires, "rentalSecurity", StringComparison.OrdinalIgnoreCase);

        var req = new CreateOrderRequest
        {
            FirstParty = a.FirstPartyName,
            SecondParty = a.SecondPartyName,
            PurchasedBy = a.FirstPartyName,
            DutyPaidBy = a.FirstPartyName,   // must exactly equal firstParty or secondParty
            ProductId = productId,
            Purpose = a.Purpose,
            ArticleCode = ScopeArticle,
            Quantity = ScopeQuantity,
            Denomination = a.Denomination,
            RentalSecurity = wantsRental ? a.ConsiderationAmount : null,
            ConsiderationPrice = wantsRental ? null : a.ConsiderationAmount,
            DoorstepDelivery = false,
            RefId = a.RefId
        };

        // Free dry-run first: catches field errors before any money moves.
        try
        {
            // Note the different shape: validate wants state/productId, not product_id.
            var validation = await client.ValidateOrderAsync(new ValidateOrderRequest
            {
                State = ScopeState,
                FirstParty = req.FirstParty,
                SecondParty = req.SecondParty,
                PurchasedBy = req.PurchasedBy,
                DutyPaidBy = req.DutyPaidBy,
                ArticleCode = req.ArticleCode,
                Article = req.ArticleCode,     // the field this endpoint actually reads
                Denomination = req.Denomination,
                RentalSecurity = req.RentalSecurity,
                ConsiderationPrice = req.ConsiderationPrice
            }, ct);
            if (validation is { Valid: false })
            {
                var reasons = validation.Errors.Count > 0
                    ? string.Join("; ", validation.Errors)
                    : "eDrafter reported the order as invalid but gave no reason.";

                logger.LogWarning("Order validation failed for {Id}: {Errors}", a.Id, reasons);
                return SpendOutcome<CreateOrderResponse>.Blocked("Validation failed: " + reasons);
            }
        }
        catch (EDrafterApiException ex)
        {
            // The call itself was rejected — a bad key, a 5xx. That is not the same as the
            // order being invalid, and saying so sends people hunting through their own data.
            logger.LogError(ex, "Could not validate order {Id}: eDrafter returned {Status}",
                a.Id, ex.StatusCode);
            return SpendOutcome<CreateOrderResponse>.Blocked(ex.Message);
        }

        var quote = await client.GetQuoteAsync(new QuoteRequest
        {
            ProductId = productId,
            Quantity = ScopeQuantity,
            Denomination = a.Denomination,
            DoorstepDelivery = false
        }, ct);

        var amountPaise = (long)Math.Round((quote?.Total ?? 0m) * 100m);

        // The idempotency key is derived, not random: the same agreement can never
        // produce two orders, however many times this is called.
        var outcome = await ledger.ExecuteAsync(
            idempotencyKey: $"order:{a.Id}",
            kind: SpendKind.Order,
            amountPaise: amountPaise,
            agreementId: a.Id,
            ourRefId: a.RefId,
            call: c => client.CreateOrderAsync(req, c),
            ct: ct);

        if (outcome.Kind == SpendResultKind.Succeeded && outcome.Value is { } created)
        {
            a.OrderIdd = created.OrderId;
            a.OrderDisplayId = created.Order?.DisplayId ?? created.Reference;
            a.Status = AgreementStatus.OrderPlaced;
            a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        else if (outcome.Kind == SpendResultKind.Unknown)
        {
            a.Status = AgreementStatus.NeedsReview;
            a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return outcome;
    }

    // ---- Stamp retrieval --------------------------------------------------

    public async Task<bool> TryAttachStampAsync(Guid agreementId, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);
        if (a.OrderIdd is null) return false;

        var stamps = await client.GetOrderStampsAsync(a.OrderIdd.Value, ct);
        if (stamps is null || stamps.Count == 0) return false;

        var first = stamps.Stamps[0];
        a.StampId = first.StampId;
        a.CertificateNo = first.CertificateNo;
        a.Status = AgreementStatus.StampReady;
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Stamp {Cert} attached to agreement {Id}", first.CertificateNo, a.Id);
        return true;
    }

    // ---- Send for signing (SPENDS, non-refundable) ------------------------

    public async Task<SpendOutcome<CreateEsignResponse>> SendForSigningAsync(
        Guid agreementId, string documentBase64, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);

        if (a.Status is not (AgreementStatus.StampReady or AgreementStatus.Preparing))
            return SpendOutcome<CreateEsignResponse>.Blocked(
                $"Agreement is {a.Status}; expected StampReady. Refusing to send.");

        if (a.EsignDocumentId is not null)
            return SpendOutcome<CreateEsignResponse>.Blocked(
                $"Already sent for signing (document {a.EsignDocumentId}). Refusing to charge again.");

        if (a.ZohoRequestId is not null)
            return SpendOutcome<CreateEsignResponse>.Blocked(
                $"This agreement already has a Zoho Sign request ({a.ZohoRequestId}). Not sending through eDrafter too.");

        var signMethod = config["Esign:DefaultSignMethod"] ?? "aadhaar_otp";
        var cost = esignPricing.Compute(signMethod, a.Signatories.Count);

        // Placeholders are per page, so the page count has to be known before sending.
        // eDrafter silently drops a placeholder for a page that does not exist, so a
        // wrong count here means missing signatures rather than an error.
        var pageCount = CountPdfPages(documentBase64);

        var req = new CreateEsignRequest
        {
            Name = $"Lease Agreement - {a.FirstPartyName} & {a.SecondPartyName}",
            SignMethod = signMethod,
            OrderId = a.OrderIdd,
            StampId = a.StampId,
            DocumentBase64 = documentBase64,
            DocumentName = "agreement.pdf",
            Reason = "Lease agreement execution",
            ExpiryDays = config.GetValue("Esign:ExpiryDays", 7),

            // No all-pages flag. Every page is enumerated explicitly instead — one base
            // Placeholder plus an extra per remaining page — which is exactly what
            // eDrafter's own dashboard sends, and the only form that honours the
            // coordinates we ask for.
            SignColor = "blue",
            SignSize = "medium",
            SigningOrder = "parallel",
            OtpRequired = true,
            ReminderCount = 2,
            ReminderFrequencyDays = 3,

            Signatories = a.Signatories.Select((s, index) =>
            {
                var (basePlaceholder, extra) = BuildPlacement(index, pageCount);
                return new EsignSignatoryDto
                {
                    Name = s.Name,
                    Email = s.Email,
                    Phone = s.Phone,
                    Placeholder = basePlaceholder,
                    ExtraPlaceholders = extra
                };
            }).ToList()
        };

        // Last chance to catch a bad layout. eDrafter returns 201 for overlapping or
        // off-page signatures just as readily as for correct ones, and the fee is charged
        // at link generation and never refunded — so a layout mistake discovered in the
        // signed PDF has already cost money. Block here instead.
        var placement = placementInspector.Inspect(req, pageCount);

        if (placement.HasOverlap)
        {
            logger.LogError("Refusing to send agreement {Id}: {Summary}", a.Id, placement.Summary);
            return SpendOutcome<CreateEsignResponse>.Blocked(placement.Summary);
        }

        logger.LogInformation("Signature placement checked: {Summary}", placement.Summary);

        logger.LogWarning(
            "Sending for signing: {Cost} — NON-REFUNDABLE, debited at link generation.",
            cost.ConfirmationText);

        var outcome = await ledger.ExecuteAsync(
            idempotencyKey: $"esign:{a.Id}",
            kind: SpendKind.Esign,
            amountPaise: cost.TotalPaise,
            agreementId: a.Id,
            ourRefId: a.RefId,
            call: c => client.CreateEsignAsync(req, c),
            ct: ct);

        if (outcome.Kind == SpendResultKind.Succeeded && outcome.Value is { } sent)
        {
            a.EsignDocumentId = sent.DocumentId;
            a.SignMethod = signMethod;
            a.EsignCostPaise = cost.TotalPaise;
            a.Status = AgreementStatus.SentForSigning;
            a.UpdatedAt = DateTime.UtcNow;

            foreach (var result in sent.Signatories)
            {
                var sig = a.Signatories.FirstOrDefault(s =>
                    string.Equals(s.Email, result.Email, StringComparison.OrdinalIgnoreCase));
                if (sig is not null)
                {
                    sig.SignUrl = result.SignUrl;
                    sig.Status = result.Status;
                }
            }
            await db.SaveChangesAsync(ct);
        }
        else if (outcome.Kind == SpendResultKind.Unknown)
        {
            a.Status = AgreementStatus.NeedsReview;
            a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return outcome;
    }

    // ---- Signature placement ----------------------------------------------

    /// <summary>
    /// The signature layout this agreement WOULD be sent with, checked for overlap.
    /// Free and sends nothing — it exists so the layout can be seen before the
    /// non-refundable e-sign fee is committed.
    /// </summary>
    public async Task<PlacementReport> PreviewPlacementAsync(
        Guid agreementId, string documentBase64, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);
        var pageCount = CountPdfPages(documentBase64);

        var req = new CreateEsignRequest
        {
            Signatories = a.Signatories.Select((s, index) =>
            {
                var (basePlaceholder, extra) = BuildPlacement(index, pageCount);
                return new EsignSignatoryDto
                {
                    Name = s.Name,
                    Email = s.Email,
                    Phone = s.Phone,
                    Placeholder = basePlaceholder,
                    ExtraPlaceholders = extra
                };
            }).ToList()
        };

        return placementInspector.Inspect(req, pageCount);
    }

    /// <summary>
    /// Signature boxes for one signatory, covering every page.
    ///
    /// Returns the BASE placeholder (page 1) plus the extras (pages 2..n). eDrafter needs
    /// both: ExtraPlaceholders on their own are ignored and the signatory is silently
    /// dropped. This split is how eDrafter's own dashboard implements "sign every page" —
    /// it sends no all-pages flag, just one box per page.
    ///
    /// The first party takes the bottom-left column and the second the bottom-right, which
    /// SignaturePosition cannot express — that is one corner shared by everyone. Further
    /// signatories alternate columns, stepping upward so boxes never collide.
    ///
    /// Coordinates are fractions of the page with a TOP-LEFT origin, so a larger yNorm is
    /// nearer the bottom. The box size matches the dashboard's own.
    /// </summary>
    private static (EsignPlaceholderDto Base, List<EsignPlaceholderDto> Extra) BuildPlacement(
        int signatoryIndex, int pageCount)
    {
        const decimal leftX = 0.02m;
        const decimal rightX = 0.55m;
        const decimal bottomY = 0.81m;
        const decimal width = 0.26m;    // dashboard default
        const decimal height = 0.09m;   // dashboard default

        // Even index -> left column (first party), odd -> right column (second party).
        var x = signatoryIndex % 2 == 0 ? leftX : rightX;

        // Each pair after the first is lifted clear of the pair below it.
        var y = bottomY - (signatoryIndex / 2) * (height + 0.02m);

        EsignPlaceholderDto Box(int page) => new()
        {
            Page = page,
            XNorm = x,
            YNorm = y,
            WNorm = width,
            HNorm = height
        };

        return (Box(1), Enumerable.Range(2, Math.Max(pageCount - 1, 0)).Select(Box).ToList());
    }

    /// <summary>
    /// Page count of a base64 PDF, read from its page-tree /Count.
    ///
    /// Falls back to 1 rather than throwing: a wrong count costs signatures on later pages,
    /// but refusing to send would block a document that is otherwise fine.
    /// </summary>
    private int CountPdfPages(string documentBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(documentBase64);
            var text = System.Text.Encoding.Latin1.GetString(bytes);

            var counts = System.Text.RegularExpressions.Regex
                .Matches(text, @"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)")
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();

            // The root page tree holds the total; nested nodes hold their own subtotals.
            if (counts.Count > 0) return Math.Max(counts.Max(), 1);

            var pageObjects = System.Text.RegularExpressions.Regex
                .Matches(text, @"/Type\s*/Page\b(?!s)").Count;

            return Math.Max(pageObjects, 1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read the PDF page count; assuming 1 page. Signatures will only " +
                "be placed on the first page.");
            return 1;
        }
    }

    // ---- Polling ----------------------------------------------------------

    /// <summary>
    /// There is no per-signatory webhook, and none for decline or expiry, so partial
    /// progress is only visible here.
    /// </summary>
    public async Task RefreshSigningStatusAsync(Guid agreementId, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);
        if (a.EsignDocumentId is null) return;

        var status = await client.GetEsignStatusAsync(a.EsignDocumentId, ct);
        if (status is null) return;

        foreach (var s in status.Signatories)
        {
            var sig = a.Signatories.FirstOrDefault(x =>
                string.Equals(x.Email, s.Email, StringComparison.OrdinalIgnoreCase));
            if (sig is null) continue;
            sig.Status = s.Status;
            sig.SignedAt = s.SignedAt;
        }

        a.Status = status.Status switch
        {
            "completed" => AgreementStatus.Signed,
            "declined" => AgreementStatus.Failed,
            _ when a.Signatories.Any(s => s.Status == "signed") => AgreementStatus.PartiallySigned,
            _ => a.Status
        };
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ---- Helpers ----------------------------------------------------------

    public async Task<Agreement> LoadAsync(Guid id, CancellationToken ct = default) =>
        await db.Agreements.Include(a => a.Signatories).FirstOrDefaultAsync(a => a.Id == id, ct)
        ?? throw new InvalidOperationException($"Agreement {id} not found");

    public Task<List<Agreement>> ListAsync(CancellationToken ct = default) =>
        db.Agreements.Include(a => a.Signatories)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

    private async Task<string> ResolveProductIdAsync(CancellationToken ct)
    {
        var products = await client.GetProductsAsync(ct);
        var match = products?.FirstOrDefault(p =>
            string.Equals(p.State, ScopeState, StringComparison.OrdinalIgnoreCase));

        return match?.Id
            ?? throw new InvalidOperationException($"No product found for state {ScopeState}");
    }
}

public sealed record CreateAgreementInput(
    string FirstPartyName, string FirstPartyEmail, string FirstPartyPhone,
    string SecondPartyName, string SecondPartyEmail, string SecondPartyPhone,
    string PropertyAddress,
    decimal ConsiderationAmount,
    decimal MonthlyRent,
    int LeaseTermMonths,
    DateOnly LeaseStartDate);

public sealed record QuotePreview(
    DutyResult Duty,
    decimal StampValue,
    decimal ServiceCharge,
    decimal Shipping,
    decimal Gst,
    decimal OrderTotal,
    decimal WalletBalance,
    bool Affordable,
    EsignCost? Esign,
    decimal GrandTotal,
    string SigningProvider);
