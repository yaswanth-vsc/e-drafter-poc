using EDrafter.Api.Data;
using EDrafter.Api.Hubs;
using EDrafter.Api.Zoho;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EDrafter.Api.Services;

/// <summary>
/// Signing through Zoho Sign, after the e-stamp has arrived from eDrafter.
///
///   1. Build the final PDF: eDrafter stamp paper + agreement.                 free, local
///   2. Lay out two signature boxes per page and check them for overlap.      free, local
///   3. POST /requests — upload the PDF as a DRAFT with two SIGN actions:     free
///        second party signs first (order 1), first party second (order 2),
///        Aadhaar eSign only.
///   4. PUT /requests/{id} — add the signature boxes to the draft.            free
///   5. POST /requests/{id}/submit — through the spend ledger.                💸 credits
///
/// Steps 3–4 are recorded as they happen, so a send that stops at step 5 (not armed,
/// no credits, a refused submit) resumes from the same draft instead of creating another.
/// Step 5 is never retried automatically; a timeout becomes NeedsReview and is settled
/// by <see cref="ReconcileAsync"/>, which asks Zoho whether the draft was actually sent.
/// </summary>
public sealed class ZohoSigningService(
    AppDbContext db,
    ZohoSignClient zoho,
    SpendLedger ledger,
    AgreementPdfBuilder pdfBuilder,
    PdfComposer composer,
    SignaturePlacementInspector inspector,
    DocumentService documents,
    IHubContext<AgreementHub> hub,
    IOptions<ZohoOptions> options,
    IConfiguration config,
    ILogger<ZohoSigningService> logger)
{
    public const string Provider = "zoho";

    private readonly ZohoOptions _o = options.Value;

    /// <summary>
    /// Where the boxes sit on the eDrafter stamp pages. Tunable, because stamp paper
    /// layouts are the issuer's, not ours — check with GET /signing-preview.
    /// </summary>
    private float StampPageBottomOffset =>
        config.GetValue("Signing:StampPageBottomOffsetPt", SignatureLayout.StampPageBottomOffset);

    private float StampPageSideOffset =>
        config.GetValue("Signing:StampPageSideOffsetPt", SignatureLayout.StampPageSideOffset);

    // ---- Preview (free, no Zoho call) ------------------------------------------

    /// <summary>
    /// The exact PDF that would be uploaded — optionally with every signature box drawn
    /// on it — plus the overlap report. Touches nothing outside this machine.
    /// </summary>
    public async Task<SigningPreview> PreviewAsync(Guid agreementId, bool drawBoxes, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);

        if (a.EsignDocumentId is null && a.SigningProvider != Provider)
        {
            a.SigningProvider = Provider;
            await db.SaveChangesAsync(ct);
        }

        var composed = ComposeOrLoad(a);
        var boxes = SignatureLayout.Build(composed.Pages, composed.StampPageCount, StampPageBottomOffset, StampPageSideOffset);
        var report = Inspect(a, boxes, composed);

        var bytes = drawBoxes
            ? composer.DrawPreview(composed, boxes, a.FirstPartyName, a.SecondPartyName)
            : composed.Bytes;

        return new SigningPreview(bytes, composed, boxes, report);
    }

    // ---- Send ------------------------------------------------------------------

    public async Task<SpendOutcome<ZohoSendResult>> SendAsync(Guid agreementId, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);

        if (a.EsignDocumentId is not null)
            return Blocked($"Already sent for signing through eDrafter (document {a.EsignDocumentId}).");

        if (a.ZohoSubmittedAt is not null)
            return Blocked($"Already sent to Zoho Sign (request {a.ZohoRequestId}). Refusing to use credits again.");

        if (a.Status is not (AgreementStatus.StampReady or AgreementStatus.Preparing))
            return Blocked($"Agreement is {a.Status}; expected StampReady. Refusing to send.");

        if (a.StampPdfPath is null || !File.Exists(a.StampPdfPath))
            return Blocked("The e-stamp PDF has not been downloaded, so there is no stamp paper to sign on. " +
                           "Refresh the stamp first.");

        var partyProblem = CheckParties(a);
        if (partyProblem is not null) return Blocked(partyProblem);

        a.SigningProvider = Provider;
        var signers = EnsureSignatories(a);
        await db.SaveChangesAsync(ct);

        // Once a draft exists, the uploaded file is fixed — lay out against THAT file, never
        // a freshly rebuilt one whose page count could differ.
        var composed = ComposeOrLoad(a);
        var boxes = SignatureLayout.Build(composed.Pages, composed.StampPageCount, StampPageBottomOffset, StampPageSideOffset);

        var report = Inspect(a, boxes, composed);
        if (report.HasOverlap)
        {
            logger.LogError("Refusing to send agreement {Id}: {Summary}", a.Id, report.Summary);
            return Blocked(report.Summary);
        }

        // 1. The draft. Free.
        if (a.ZohoRequestId is null)
        {
            var created = await CreateDraftAsync(a, composed, signers, ct);
            if (created is not null) return Blocked(created);
        }
        else
        {
            var resumed = await ResumeDraftAsync(a, signers, ct);
            if (resumed is not null) return Blocked(resumed);
        }

        // 2. The boxes. Free, and only once — a second PUT would add a second set.
        if (a.ZohoFieldsPlacedAt is null)
        {
            var placed = await PlaceFieldsAsync(a, signers, boxes, ct);
            if (placed is not null) return Blocked(placed);
        }

        // 3. Submit. 💸 Consumes credits, emails the second party.
        var zohoSigners = ToZoho(signers);
        var amountPaise = _o.CreditsPerAgreement * _o.CreditPricePaise;
        var key = await SubmitKeyAsync(a.Id, ct);

        logger.LogWarning(
            "Ready to submit Zoho request {Request} for agreement {Id} ({Credits} credits, Aadhaar eSign only, " +
            "second party first; not refunded on decline). Goes ahead only if Zoho:ArmSpending is true.",
            a.ZohoRequestId, a.Id, _o.CreditsPerAgreement);

        var outcome = await ledger.ExecuteAsync(
            idempotencyKey: key,
            kind: SpendKind.ZohoEsign,
            amountPaise: amountPaise,
            agreementId: a.Id,
            ourRefId: a.RefId,
            call: c => SubmitCallAsync(a.ZohoRequestId!, zohoSigners, c),
            ct: ct);

        switch (outcome.Kind)
        {
            case SpendResultKind.Succeeded:
                a.ZohoSubmittedAt = DateTime.UtcNow;
                a.Status = AgreementStatus.SentForSigning;
                a.SignMethod = "zoho_aadhaar_esign";
                a.EsignCostPaise = amountPaise;
                a.UpdatedAt = DateTime.UtcNow;
                foreach (var s in signers)
                    s.Status = s.Role == SignatureLayout.SecondParty ? "sent" : "waiting";
                await db.SaveChangesAsync(ct);

                await NotifyAsync(a, $"Sent to Zoho Sign. {a.SecondPartyName} signs first, then {a.FirstPartyName}.", ct);
                break;

            case SpendResultKind.Unknown:
                a.Status = AgreementStatus.NeedsReview;
                a.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                break;

            case SpendResultKind.Blocked:
                // Almost always "not armed". Say what exists so it can be checked in Zoho.
                return Blocked(
                    $"{outcome.Message} Zoho draft {a.ZohoRequestId}: {boxes.Count} signature boxes on " +
                    $"{composed.Pages.Count} pages ({composed.StampPageCount} stamp).");
        }

        return outcome;
    }

    private async Task<string?> CreateDraftAsync(
        Agreement a, ComposedPdf composed, List<Signatory> signers, CancellationToken ct)
    {
        ZohoCreatedRequest created;
        try
        {
            created = await zoho.CreateRequestAsync(
                composed.Bytes, $"{a.RefId}.pdf", RequestName(a), Notes(a), ToZoho(signers), ct);
        }
        catch (ZohoException ex)
        {
            logger.LogError(ex, "Zoho refused the draft for agreement {Id}.", a.Id);
            return $"Zoho refused to create the draft: {ex.Message} Nothing was charged — drafts are free.";
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            logger.LogError(ex, "No answer from Zoho creating the draft for agreement {Id}.", a.Id);
            return "Could not reach Zoho to create the draft. Nothing is charged for a draft, but one may " +
                   $"have been created — look for \"{RequestName(a)}\" in Zoho Sign and delete it before sending again.";
        }

        a.ZohoRequestId = created.RequestId;
        a.ZohoDocumentId = created.DocumentId;
        a.FinalPdfPath = composed.Path;
        a.Status = AgreementStatus.Preparing;
        a.UpdatedAt = DateTime.UtcNow;
        AdoptActionIds(signers, created.Actions);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Zoho draft {Request} created for agreement {Id} (document {Doc}).",
            created.RequestId, a.Id, created.DocumentId);

        return signers.Any(s => s.ZohoActionId is null)
            ? $"Zoho created draft {created.RequestId} but did not return an action id for every signer. " +
              "Check the recipients on the draft in Zoho Sign."
            : null;
    }

    private async Task<string?> ResumeDraftAsync(Agreement a, List<Signatory> signers, CancellationToken ct)
    {
        ZohoRequestState state;
        try
        {
            state = await zoho.GetRequestStateAsync(a.ZohoRequestId!, ct);
        }
        catch (ZohoException ex)
        {
            return $"Could not read Zoho draft {a.ZohoRequestId}: {ex.Message}";
        }

        if (!state.IsDraft)
        {
            return $"Zoho request {a.ZohoRequestId} is \"{state.Status}\", not a draft, so it may already have " +
                   "been sent. Not submitting again — use Refresh signing status, or Reconcile.";
        }

        if (signers.Any(s => s.ZohoActionId is null))
        {
            AdoptActionIds(signers, state.Actions);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Resuming Zoho draft {Request} for agreement {Id}.", a.ZohoRequestId, a.Id);
        return null;
    }

    private async Task<string?> PlaceFieldsAsync(
        Agreement a, List<Signatory> signers, List<SignatureBox> boxes, CancellationToken ct)
    {
        var actionFor = signers.ToDictionary(s => s.Role!, s => s.ZohoActionId!);

        var fields = boxes.Select(b => new ZohoSignatureField(
            ActionId: actionFor[b.Role],
            FieldName: $"{(b.Role == SignatureLayout.FirstParty ? "FirstParty" : "SecondParty")}_Signature_p{b.PageIndex + 1}",
            PageNo: b.PageIndex,
            X: b.X, Y: b.Y, W: b.W, H: b.H,
            PageWidth: b.PageWidth, PageHeight: b.PageHeight)).ToList();

        try
        {
            await zoho.AddSignatureFieldsAsync(a.ZohoRequestId!, a.ZohoDocumentId!, ToZoho(signers), fields, ct);
        }
        catch (ZohoException ex)
        {
            logger.LogError(ex, "Zoho refused the signature fields for agreement {Id}.", a.Id);
            return $"Zoho refused the signature boxes on draft {a.ZohoRequestId}: {ex.Message} Nothing was charged. " +
                   "If Zoho complains about the field format, try Zoho:FieldsShape = \"grouped\".";
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            // Unknown whether the boxes were added. Re-adding would double them, so stop and look.
            logger.LogError(ex, "No answer from Zoho placing fields for agreement {Id}.", a.Id);
            return $"No answer from Zoho while adding the signature boxes to draft {a.ZohoRequestId}. Open the " +
                   "draft in Zoho Sign: if the boxes are there, they were added. Nothing was charged.";
        }

        a.ZohoFieldsPlacedAt = DateTime.UtcNow;
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Count} signature boxes placed on Zoho draft {Request}.", fields.Count, a.ZohoRequestId);
        return null;
    }

    private async Task<SpendCallResult<ZohoSendResult>> SubmitCallAsync(
        string requestId, IReadOnlyList<ZohoSigner> signers, CancellationToken ct)
    {
        try
        {
            var node = await zoho.SubmitAsync(requestId, signers, ct);
            var status = node["requests"]?["request_status"]?.ToString();
            return SpendCallResult<ZohoSendResult>.Ok(new ZohoSendResult(requestId, status), requestId);
        }
        catch (ZohoException ex) when (ex.IsServerError)
        {
            // 5xx: Zoho may have sent it before failing. Unknown, not failed.
            return SpendCallResult<ZohoSendResult>.ServerError(ex.Message);
        }
        catch (ZohoException ex)
        {
            // An answer that says no — 13001 no credits, 9043 extra key, 4008 invalid action.
            return SpendCallResult<ZohoSendResult>.ClientError(ex.Message);
        }
    }

    /// <summary>
    /// The idempotency key for this agreement's submit. A submit Zoho definitely refused
    /// (FailedSafe) spent nothing, so the next send gets a fresh key; anything else —
    /// succeeded, in flight, unknown — hands back the existing key, and the ledger refuses.
    /// </summary>
    private async Task<string> SubmitKeyAsync(Guid agreementId, CancellationToken ct)
    {
        var baseKey = $"zoho-submit:{agreementId}";

        var previous = await db.SpendAttempts
            .Where(x => x.IdempotencyKey == baseKey || x.IdempotencyKey.StartsWith(baseKey + ":retry"))
            .ToListAsync(ct);

        var live = previous.FirstOrDefault(x =>
            x.Status is not (SpendStatus.FailedSafe or SpendStatus.ConfirmedNotPlaced));

        if (live is not null) return live.IdempotencyKey;
        return previous.Count == 0 ? baseKey : $"{baseKey}:retry{previous.Count}";
    }

    /// <summary>
    /// Forgets an UNSENT Zoho draft so the next send uploads a fresh one — for when the
    /// boxes landed wrong and a layout setting was changed. Local only: it does not call
    /// Zoho, so delete the old draft in Zoho Sign by hand. Refused once submitted.
    /// </summary>
    public async Task<string?> ResetDraftAsync(Guid agreementId, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);

        if (a.ZohoSubmittedAt is not null)
            return $"Zoho request {a.ZohoRequestId} was already submitted — it cannot be reset.";

        if (await db.SpendAttempts.AnyAsync(x => x.AgreementId == a.Id && x.Kind == SpendKind.ZohoEsign &&
                                                 (x.Status == SpendStatus.Unknown || x.Status == SpendStatus.Attempting), ct))
            return "A submit for this agreement has an unknown outcome. Reconcile first.";

        var old = a.ZohoRequestId;
        a.ZohoRequestId = null;
        a.ZohoDocumentId = null;
        a.ZohoFieldsPlacedAt = null;
        a.FinalPdfPath = null;
        foreach (var s in a.Signatories) s.ZohoActionId = null;
        if (a.Status == AgreementStatus.Preparing) a.Status = AgreementStatus.StampReady;
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogWarning("Zoho draft {Request} forgotten for agreement {Id}. Delete it in Zoho Sign.", old, a.Id);
        return null;
    }

    // ---- Status (webhook and polling share this) ---------------------------------

    /// <summary>Free. Reads the request from Zoho and applies it.</summary>
    public async Task RefreshAsync(Guid agreementId, CancellationToken ct = default)
    {
        var a = await LoadAsync(agreementId, ct);
        if (a.ZohoRequestId is null || a.ZohoSubmittedAt is null) return;

        var state = await zoho.GetRequestStateAsync(a.ZohoRequestId, ct);
        await ApplyAsync(a, state, operation: null, ct);
    }

    /// <summary>
    /// Moves the agreement forward from what Zoho reports. Only ever forward, so a late or
    /// out-of-order webhook cannot drag Signed back to PartiallySigned.
    /// </summary>
    public async Task ApplyAsync(Agreement a, ZohoRequestState state, string? operation, CancellationToken ct = default)
    {
        var previous = a.Status;
        var op = operation?.ToLowerInvariant() ?? "";
        var requestStatus = state.Status?.ToLowerInvariant();
        var changed = false;

        foreach (var action in state.Actions)
        {
            var sig = a.Signatories.FirstOrDefault(s => s.ZohoActionId == action.ActionId)
                      ?? a.Signatories.FirstOrDefault(s =>
                          string.Equals(s.Email, action.Email, StringComparison.OrdinalIgnoreCase));
            if (sig is null || string.IsNullOrWhiteSpace(action.Status)) continue;

            var status = action.Status.ToLowerInvariant();
            if (sig.Status == status) continue;

            sig.Status = status;
            if (status == "signed") sig.SignedAt ??= DateTime.UtcNow;
            changed = true;
        }

        if (op.Contains("forward") || op.Contains("reassign"))
        {
            // The person who signs is no longer the person named on the e-stamp.
            logger.LogWarning(
                "Zoho request {Request} was REASSIGNED to another recipient. The signer no longer matches " +
                "the party on the e-stamp — verify before relying on agreement {Id}.", a.ZohoRequestId, a.Id);
        }

        AgreementStatus? next =
            op.Contains("complet") || requestStatus == "completed" ? AgreementStatus.Signed
            : op.Contains("reject") || op.Contains("declin") || requestStatus is "declined" or "rejected" ? AgreementStatus.Failed
            : op.Contains("expir") || requestStatus == "expired" ? AgreementStatus.Failed
            : op.Contains("recall") || requestStatus == "recalled" ? AgreementStatus.Cancelled
            : a.Signatories.Any(s => s.Status == "signed") ? AgreementStatus.PartiallySigned
            : null;

        var terminal = a.Status is AgreementStatus.Signed or AgreementStatus.Failed or AgreementStatus.Cancelled;
        if (next is { } n && !terminal && n != a.Status)
        {
            a.Status = n;
            changed = true;
        }

        if (a.Status == AgreementStatus.Failed && previous != AgreementStatus.Failed)
        {
            logger.LogWarning("Zoho request {Request} ended without completion ({Op}/{Status}). " +
                              "The Zoho credits are NOT refunded.", a.ZohoRequestId, operation, state.Status);
        }

        if (a.Status == AgreementStatus.Signed && a.SignedPdfPath is null)
        {
            try
            {
                var signed = await zoho.DownloadSignedPdfAsync(a.ZohoRequestId!, ct);
                a.SignedPdfPath = documents.SaveSigned(a.Id, signed);
                changed = true;
                logger.LogInformation("Signed PDF for agreement {Id} archived ({Bytes} bytes).", a.Id, signed.Length);
            }
            catch (Exception ex)
            {
                // Polling tries again; the agreement stays Signed either way.
                logger.LogError(ex, "Could not download the signed PDF for agreement {Id}; polling will retry.", a.Id);
            }
        }

        if (!changed) return;

        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await NotifyAsync(a, a.Status switch
        {
            AgreementStatus.Signed => "Both parties have signed. The signed PDF is ready.",
            AgreementStatus.PartiallySigned => $"{a.SecondPartyName} has signed; waiting on {a.FirstPartyName}.",
            AgreementStatus.Failed => "The signing request ended without completion.",
            AgreementStatus.Cancelled => "The signing request was recalled.",
            _ => "Signing status updated."
        }, ct);
    }

    // ---- Reconcile ------------------------------------------------------------------

    /// <summary>
    /// Settles a submit whose outcome is Unknown. The draft id was saved BEFORE submit, so
    /// Zoho can simply be asked: still a draft means it was not sent; anything else means
    /// it was. Never resends.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(SpendAttempt attempt, CancellationToken ct = default)
    {
        if (attempt.AgreementId is not { } id)
            return new ReconcileResult(attempt.Id, attempt.Kind, false, "No agreement linked.");

        var a = await LoadAsync(id, ct);
        if (a.ZohoRequestId is null)
            return new ReconcileResult(attempt.Id, attempt.Kind, false,
                "No Zoho request id recorded — check Zoho Sign by hand.");

        var state = await zoho.GetRequestStateAsync(a.ZohoRequestId, ct);

        if (state.IsDraft)
        {
            await ledger.MarkNotPlacedAsync(attempt.Id,
                $"Zoho request {a.ZohoRequestId} is still a draft — the submit never took effect.", ct);

            a.Status = AgreementStatus.Preparing;
            a.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return new ReconcileResult(attempt.Id, attempt.Kind, true,
                $"Zoho request {a.ZohoRequestId} is still a draft: nothing was sent or charged. It can be sent again.");
        }

        await ledger.AdoptAsync(attempt.Id, a.ZohoRequestId, ct);

        a.ZohoSubmittedAt ??= DateTime.UtcNow;
        a.SignMethod = "zoho_aadhaar_esign";
        a.EsignCostPaise ??= attempt.AmountPaise;
        a.Status = AgreementStatus.SentForSigning;
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await ApplyAsync(a, state, operation: null, ct);

        return new ReconcileResult(attempt.Id, attempt.Kind, true,
            $"Zoho request {a.ZohoRequestId} is \"{state.Status}\": it was sent — adopted. The credits were used.");
    }

    // ---- Helpers ----------------------------------------------------------------------

    private ComposedPdf ComposeOrLoad(Agreement a)
    {
        var stampPages = PdfComposer.ReadPageSizes(File.ReadAllBytes(
            a.StampPdfPath ?? throw new InvalidOperationException("The e-stamp PDF has not been downloaded yet."))).Count;

        if (a.ZohoRequestId is not null && a.FinalPdfPath is not null && File.Exists(a.FinalPdfPath))
            return composer.Load(a.FinalPdfPath, stampPages);

        return composer.Compose(a, pdfBuilder.Build(a));
    }

    private PlacementReport Inspect(Agreement a, List<SignatureBox> boxes, ComposedPdf composed) =>
        inspector.Inspect(SignatureLayout.ToInspectable(boxes, a.FirstPartyName, a.SecondPartyName), composed.Pages.Count);

    private static string? CheckParties(Agreement a)
    {
        if (string.IsNullOrWhiteSpace(a.FirstPartyEmail) || string.IsNullOrWhiteSpace(a.SecondPartyEmail))
            return "Both parties need an email address — Zoho sends the signing link by email.";

        if (string.Equals(a.FirstPartyEmail.Trim(), a.SecondPartyEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Both parties have the same email address. Zoho needs a separate signer for each party.";

        return null;
    }

    /// <summary>
    /// Both parties sign, whatever Esign:Signatories says — that setting is an eDrafter cost
    /// lever. Returns them in signing order: second party, then first party.
    /// </summary>
    private static List<Signatory> EnsureSignatories(Agreement a)
    {
        Signatory Ensure(string role, string name, string email, string phone, int order)
        {
            var s = a.Signatories.FirstOrDefault(x => x.Role == role)
                    ?? a.Signatories.FirstOrDefault(x => x.Role is null &&
                           string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

            if (s is null)
            {
                s = new Signatory();
                a.Signatories.Add(s);
            }

            s.Role = role;
            s.Name = name;
            s.Email = email.Trim();
            s.Phone = phone;
            s.SigningOrder = order;
            return s;
        }

        var second = Ensure(SignatureLayout.SecondParty, a.SecondPartyName, a.SecondPartyEmail, a.SecondPartyPhone, 1);
        var first = Ensure(SignatureLayout.FirstParty, a.FirstPartyName, a.FirstPartyEmail, a.FirstPartyPhone, 2);
        return [second, first];
    }

    private static void AdoptActionIds(List<Signatory> signers, IReadOnlyList<ZohoAction> actions)
    {
        foreach (var s in signers)
        {
            var match = actions.FirstOrDefault(x => string.Equals(x.Email, s.Email, StringComparison.OrdinalIgnoreCase))
                        ?? actions.FirstOrDefault(x => x.SigningOrder == s.SigningOrder);
            if (match is not null) s.ZohoActionId = match.ActionId;
        }
    }

    private static List<ZohoSigner> ToZoho(IEnumerable<Signatory> signers) =>
        signers.Select(s => new ZohoSigner(s.Role!, s.Name, s.Email, s.Phone, s.SigningOrder ?? 0, s.ZohoActionId))
            .ToList();

    private static string RequestName(Agreement a) =>
        $"Lease Agreement - {a.FirstPartyName} & {a.SecondPartyName} - {a.RefId}";

    private static string Notes(Agreement a) =>
        "Lease agreement on e-stamp paper" +
        (string.IsNullOrWhiteSpace(a.CertificateNo) ? "" : $" (certificate {a.CertificateNo})") +
        ". Please sign with Aadhaar eSign: an OTP will be sent to the mobile number linked to your Aadhaar.";

    private async Task NotifyAsync(Agreement a, string message, CancellationToken ct) =>
        await hub.Clients.All.SendAsync("AgreementUpdated", new
        {
            id = a.Id,
            status = a.Status.ToString(),
            signatories = a.Signatories.Select(s => new { s.Name, s.Status }),
            message
        }, ct);

    private async Task<Agreement> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Agreements.Include(x => x.Signatories).FirstOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new InvalidOperationException($"Agreement {id} not found");

    private static SpendOutcome<ZohoSendResult> Blocked(string message) => SpendOutcome<ZohoSendResult>.Blocked(message);
}

public sealed record ZohoSendResult(string RequestId, string? RequestStatus);

public sealed record SigningPreview(
    byte[] Pdf, ComposedPdf Composed, List<SignatureBox> Boxes, PlacementReport Report);
