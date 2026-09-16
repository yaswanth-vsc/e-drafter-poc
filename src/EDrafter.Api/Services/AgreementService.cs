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
        (config["Esign:Signatories"] ?? "second").Trim().ToLowerInvariant();

    // ---- Draft ------------------------------------------------------------

    public async Task<Agreement> CreateDraftAsync(CreateAgreementInput input, CancellationToken ct = default)
    {
        var d = duty.Calculate(ScopeArticle, input.ConsiderationAmount);

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
                Phone = agreement.FirstPartyPhone
            });
        }

        if (SignatoryScope is "both" or "second")
        {
            agreement.Signatories.Add(new Signatory
            {
                Name = agreement.SecondPartyName,
                Email = agreement.SecondPartyEmail,
                Phone = agreement.SecondPartyPhone
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
            "Draft {Id} created. refId={RefId} duty=₹{Duty} (capped: {Capped})",
            agreement.Id, agreement.RefId, d.Duty, d.WasCapped);

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

        var signMethod = config["Esign:DefaultSignMethod"] ?? "phone_otp";
        var esign = esignPricing.Compute(signMethod, a.Signatories.Count);
        var d = duty.Calculate(ScopeArticle, a.ConsiderationAmount);

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
            GrandTotal: (quote?.Total ?? 0) + esign.TotalRupees);
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
            ConsiderationPrice = a.ConsiderationAmount,
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

        var signMethod = config["Esign:DefaultSignMethod"] ?? "phone_otp";
        var cost = esignPricing.Compute(signMethod, a.Signatories.Count);

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
            SignaturePosition = config["Esign:SignaturePosition"] ?? "bottom-left",
            Signatories = a.Signatories.Select(s => new EsignSignatoryDto
            {
                Name = s.Name,
                Email = s.Email,
                Phone = s.Phone
            }).ToList()
        };

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
    EsignCost Esign,
    decimal GrandTotal);
