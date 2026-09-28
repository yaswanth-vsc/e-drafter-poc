using EDrafter.Api.Data;
using EDrafter.Api.EDrafterClient;
using EDrafter.Api.Hubs;
using EDrafter.Api.Services;
using EDrafter.Api.Zoho;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Real credentials live here, never in appsettings.json, which is committed.
// Gitignored and optional, so a fresh clone still runs against the mock.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ---------------------------------------------------------------------------
// Database. PostgreSQL is the target; SQLite is the local fallback so the POC
// runs without a server installed. Schema and logic are identical either way.
// ---------------------------------------------------------------------------
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("Default")
                       ?? "Data Source=edrafter-poc.db";

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        opt.UseNpgsql(connectionString);
    else
        opt.UseSqlite(connectionString);
});

// ---------------------------------------------------------------------------
// eDrafter client.
//
// NOTE ON RETRIES: there is deliberately NO retry policy registered here.
// Polly's retry-on-everything is precisely the bug that double-charges, because
// POST /orders and POST /esign are not idempotent. Free GETs are cheap to repeat
// by hand; the spending calls must never repeat themselves.
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient<EDrafterApiClient>((sp, http) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["EDrafter:BaseUrl"] ?? "http://localhost:5099/api/v1";
    http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    http.DefaultRequestHeaders.Add("x-api-key", cfg["EDrafter:ApiKey"] ?? "mock-key");
    http.Timeout = TimeSpan.FromSeconds(cfg.GetValue("EDrafter:TimeoutSeconds", 30));
})
// Logs every eDrafter request and response in full — url, headers, payload, status,
// duration. Set EDrafter:LogPayloads=false to silence it.
.AddHttpMessageHandler<EDrafterLoggingHandler>();

builder.Services.AddTransient<EDrafterLoggingHandler>();

// ---------------------------------------------------------------------------
// Zoho Sign — signing only; the stamp still comes from eDrafter.
//
// Same rule as eDrafter: NO retry policy. POST /requests/{id}/submit consumes credits and
// is not idempotent. Nothing calls Zoho at startup; the first call is a user pressing
// Send, and /submit additionally needs Zoho:ArmSpending = true.
// ---------------------------------------------------------------------------
builder.Services.Configure<ZohoOptions>(builder.Configuration.GetSection(ZohoOptions.Section));

if (string.Equals(builder.Configuration["Zoho:AuthMode"], ZohoAuthModes.DevToken, StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IZohoTokenProvider, ZohoStaticTokenProvider>();
else
    builder.Services.AddSingleton<IZohoTokenProvider, ZohoRefreshTokenProvider>();

builder.Services.AddHttpClient<ZohoSignClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<ZohoOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
    http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
});

builder.Services.AddSingleton<PdfComposer>();
builder.Services.AddScoped<ZohoSigningService>();
builder.Services.AddScoped<ZohoWebhookProcessor>();

builder.Services.AddScoped<SpendGuard>();
builder.Services.AddScoped<SpendLedger>();
builder.Services.AddScoped<DutyCalculator>();
builder.Services.AddScoped<EsignPricing>();
builder.Services.AddSingleton<SignaturePlacementInspector>();
builder.Services.AddScoped<AgreementService>();
builder.Services.AddScoped<Reconciler>();
builder.Services.AddSingleton<AgreementPdfBuilder>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<WebhookProcessor>();
builder.Services.AddSingleton<WebhookSecretStore>();

// Registers our callback with eDrafter at startup so a local run needs no manual curl.
// Mock mode only by default — see WebhookRegistrar for why.
builder.Services.AddHostedService<WebhookRegistrar>();

// Webhooks are primary, polling is the fallback. Never trust a single mechanism —
// and several states (decline, expiry, partial signing) have no webhook at all.
builder.Services.AddHostedService<PollingService>();

builder.Services.AddSignalR();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:4200")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));   // required for the SignalR handshake

var app = builder.Build();

// Create the schema on startup. Migrations come later; this keeps the POC runnable.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated never alters an existing database; this adds the Zoho columns to one.
    await SchemaUpgrader.UpgradeAsync(db, scope.ServiceProvider.GetRequiredService<ILogger<Program>>());
}

app.UseCors();

var log = app.Services.GetRequiredService<ILogger<Program>>();
var mode = app.Configuration["EDrafter:Mode"] ?? "Mock";
var armed = app.Configuration.GetValue("EDrafter:ArmSpending", false);
log.LogWarning("eDrafter POC API starting. Mode={Mode} ArmSpending={Armed} BaseUrl={Url}",
    mode, armed, app.Configuration["EDrafter:BaseUrl"]);

var zohoOptions = app.Services.GetRequiredService<IOptions<ZohoOptions>>().Value;
log.LogWarning(
    "Signing provider={Provider}. Zoho: BaseUrl={Url} Credentials={Creds} ArmSpending={Armed} " +
    "Providers=[{Ids}] Fields={Shape}/{Coords} WebhookSecret={Secret}",
    app.Configuration["Signing:Provider"] ?? "zoho", zohoOptions.BaseUrl,
    zohoOptions.HasCredentials ? "set" : "MISSING", zohoOptions.ArmSpending,
    string.Join(",", zohoOptions.EffectiveCloudProviderIds), zohoOptions.FieldsShape, zohoOptions.CoordinateMode,
    string.IsNullOrWhiteSpace(zohoOptions.WebhookSecret) ? "not set" : "set");

var api = app.MapGroup("/api");

// ---------------------------------------------------------------------------
// Health and spend status
// ---------------------------------------------------------------------------
api.MapGet("/health", async (EDrafterApiClient client, SpendGuard guard, IServiceProvider sp, CancellationToken ct) =>
{
    var account = await client.GetAccountAsync(ct);
    var summary = await guard.SummaryAsync(ct);
    var zo = sp.GetRequiredService<IOptions<ZohoOptions>>().Value;
    return Results.Ok(new
    {
        ok = true,
        edrafter = new { account?.Company, account?.Balance },
        spend = summary,
        // Configuration only — this does not call Zoho.
        zoho = new
        {
            provider = sp.GetRequiredService<IConfiguration>()["Signing:Provider"] ?? "zoho",
            credentials = zo.HasCredentials,
            armed = zo.ArmSpending,
            allowedCloudProviderIds = zo.EffectiveCloudProviderIds,
            webhookSecret = !string.IsNullOrWhiteSpace(zo.WebhookSecret)
        }
    });
});

api.MapGet("/spend/summary", async (SpendGuard guard, CancellationToken ct) =>
    Results.Ok(await guard.SummaryAsync(ct)));

api.MapGet("/spend/attempts", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await db.SpendAttempts.OrderByDescending(a => a.CreatedAt).ToListAsync(ct)));

// ---------------------------------------------------------------------------
// Catalog passthrough — the form is built from these, not from hardcoded rules
// ---------------------------------------------------------------------------
api.MapGet("/rules", async (EDrafterApiClient client, IConfiguration cfg, CancellationToken ct) =>
{
    var state = cfg["Scope:State"] ?? "Karnataka";
    var article = cfg["Scope:ArticleCode"] ?? "30(1)(i)";
    var rules = await client.GetStateRulesAsync(state, article, ct);
    return rules is null ? Results.NotFound() : Results.Ok(new { state, article, rules });
});

// ---------------------------------------------------------------------------
// Agreements
// ---------------------------------------------------------------------------
api.MapPost("/agreements", async (
    CreateAgreementInput input, AgreementService svc, CancellationToken ct) =>
{
    try
    {
        var a = await svc.CreateDraftAsync(input, ct);
        return Results.Created($"/api/agreements/{a.Id}", ToDto(a));
    }
    catch (StampDutyException ex)
    {
        // eDrafter declined to price it — the amount is below the article's minimum.
        // Their wording is written for end users, so pass it through as a 400 rather
        // than letting it surface as a 500 with a stack trace.
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["considerationAmount"] = [ex.Message]
        });
    }
});

api.MapGet("/agreements", async (AgreementService svc, CancellationToken ct) =>
    Results.Ok((await svc.ListAsync(ct)).Select(ToDto)));

api.MapGet("/agreements/{id:guid}", async (Guid id, AgreementService svc, CancellationToken ct) =>
{
    try { return Results.Ok(ToDto(await svc.LoadAsync(id, ct))); }
    catch (InvalidOperationException) { return Results.NotFound(); }
});

/// The cost preview. Free — no order placed, no wallet touched.
api.MapGet("/agreements/{id:guid}/quote", async (Guid id, AgreementService svc, CancellationToken ct) =>
{
    try
    {
        var q = await svc.GetQuoteAsync(id, ct);
        return Results.Ok(new
        {
            duty = new
            {
                article = q.Duty.ArticleCode,
                consideration = q.Duty.Amount,
                amount = q.Duty.Duty,
                basis = q.Duty.Basis,
                pct = q.Duty.Rule?.Percent,
                cap = q.Duty.Rule?.Max,
                minInput = q.Duty.Rule?.MinInput,
                requires = q.Duty.Rule?.Requires,

                // False means the figure did not come from eDrafter, so the order may be
                // charged something else. Surfaced so the UI can say so.
                fromEDrafter = q.Duty.FromEDrafter,
                warning = q.Duty.Warning
            },
            order = new
            {
                stampValue = q.StampValue,
                serviceCharge = q.ServiceCharge,
                shipping = q.Shipping,
                gst = q.Gst,
                total = q.OrderTotal
            },
            // Null for Zoho: its cost is not in the quote yet.
            esign = q.Esign is null ? null : new
            {
                signMethod = q.Esign.SignMethod,
                signatories = q.Esign.SignatoryCount,
                ratePerSignatory = q.Esign.RateRupees,
                total = q.Esign.TotalRupees,
                gst = "not charged on e-sign",
                confirmation = q.Esign.ConfirmationText
            },
            signing = q.SigningProvider == ZohoSigningService.Provider
                ? new
                {
                    provider = "zoho",
                    method = "Aadhaar eSign",
                    order = "Second party signs first, then first party",
                    note = "Zoho Sign cost is not included in this quote.",
                    confirmation =
                        "This sends the stamped agreement to Zoho Sign. The second party is emailed first; " +
                        "the first party only after the second party has signed. Both must sign with Aadhaar " +
                        "eSign. It uses Zoho credits, which are not refunded if a party declines or the " +
                        "request expires."
                }
                : null,
            grandTotal = q.GrandTotal,
            walletBalance = q.WalletBalance,
            affordable = q.Affordable
        });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

/// 💸 SPENDS. Guarded by the ledger — cannot run twice for the same agreement.
api.MapPost("/agreements/{id:guid}/order", async (Guid id, AgreementService svc, CancellationToken ct) =>
{
    var outcome = await svc.PlaceOrderAsync(id, ct);
    return OutcomeToResult(outcome, o => new { orderId = o.OrderId, total = o.TotalAmount });
});

api.MapPost("/agreements/{id:guid}/refresh-stamp", async (Guid id, AgreementService svc, CancellationToken ct) =>
{
    var attached = await svc.TryAttachStampAsync(id, ct);
    return Results.Ok(new { stampReady = attached });
});

/// 💸 SPENDS at link generation, per signatory, non-refundable.
api.MapPost("/agreements/{id:guid}/send-for-signing", async (
    Guid id, SendForSigningRequest body, AgreementService svc, CancellationToken ct) =>
{
    var outcome = await svc.SendForSigningAsync(id, body.DocumentBase64, ct);
    return OutcomeToResult(outcome, o => new
    {
        documentId = o.DocumentId,
        signatories = o.Signatories.Select(s => new { s.Name, s.Email, s.SignUrl })
    });
});

/// The signature layout, checked for overlap. Free — sends nothing, spends nothing.
/// Call it before send-for-signing: eDrafter returns 201 for an overlapping layout just
/// as it does for a correct one, and the e-sign fee is not refunded.
api.MapPost("/agreements/{id:guid}/placement-preview", async (
    Guid id, SendForSigningRequest body, AgreementService svc, CancellationToken ct) =>
{
    try
    {
        var report = await svc.PreviewPlacementAsync(id, body.DocumentBase64, ct);
        return Results.Ok(new
        {
            pageCount = report.PageCount,
            totalBoxes = report.TotalBoxes,
            hasOverlap = report.HasOverlap,
            summary = report.Summary,
            issues = report.Issues.Select(i => new
            {
                kind = i.Kind,
                page = i.Page,
                signatories = i.Signatories,
                message = i.Message
            }),
            pages = report.Pages.Select(p => new
            {
                page = p.Page,
                boxes = p.Boxes.Select(b => new
                {
                    signatory = b.Signatory,
                    xNorm = b.XNorm, yNorm = b.YNorm, wNorm = b.WNorm, hNorm = b.HNorm
                })
            })
        });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapPost("/agreements/{id:guid}/refresh-signing", async (
    Guid id, AgreementService svc, ZohoSigningService zoho, CancellationToken ct) =>
{
    var current = await svc.LoadAsync(id, ct);
    if (current.ZohoRequestId is not null)
        await zoho.RefreshAsync(id, ct);
    else
        await svc.RefreshSigningStatusAsync(id, ct);
    return Results.Ok(ToDto(await svc.LoadAsync(id, ct)));
});

// ---------------------------------------------------------------------------
// Documents
// ---------------------------------------------------------------------------

/// Generates (or regenerates) the agreement PDF. Free — nothing is sent.
api.MapPost("/agreements/{id:guid}/generate-pdf", async (
    Guid id, AgreementService svc, DocumentService docs, AppDbContext db, CancellationToken ct) =>
{
    var a = await svc.LoadAsync(id, ct);
    var result = docs.BuildAgreementDocument(a);

    if (!result.Success)
        return Results.BadRequest(new { error = result.Error });

    a.AgreementPdfPath = result.Path;
    a.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        sizeBytes = result.Bytes!.Length,
        sizeKb = Math.Round(result.Bytes.Length / 1024.0, 1),
        limitKb = DocumentService.MaxPdfBytes / 1024,
        withinLimit = true
    });
});

/// Preview the generated agreement in the browser.
api.MapGet("/agreements/{id:guid}/pdf", async (
    Guid id, AgreementService svc, DocumentService docs, CancellationToken ct) =>
{
    var a = await svc.LoadAsync(id, ct);
    var result = docs.BuildAgreementDocument(a);
    return result.Success
        ? Results.File(result.Bytes!, "application/pdf", $"agreement-{a.RefId}.pdf")
        : Results.BadRequest(new { error = result.Error });
});

/// The completed, signed document.
api.MapGet("/agreements/{id:guid}/signed-pdf", async (
    Guid id, AgreementService svc, CancellationToken ct) =>
{
    var a = await svc.LoadAsync(id, ct);
    if (a.SignedPdfPath is null || !File.Exists(a.SignedPdfPath))
        return Results.NotFound(new { error = "No signed document yet." });

    var bytes = await File.ReadAllBytesAsync(a.SignedPdfPath, ct);
    return Results.File(bytes, "application/pdf", $"signed-{a.RefId}.pdf");
});

/// The e-stamp certificate, as issued.
api.MapGet("/agreements/{id:guid}/stamp-pdf", async (
    Guid id, AgreementService svc, CancellationToken ct) =>
{
    var a = await svc.LoadAsync(id, ct);
    if (a.StampPdfPath is null || !File.Exists(a.StampPdfPath))
        return Results.NotFound(new { error = "No stamp yet." });

    var bytes = await File.ReadAllBytesAsync(a.StampPdfPath, ct);
    return Results.File(bytes, "application/pdf", $"stamp-{a.CertificateNo}.pdf");
});

/// 💸 SPENDS. Generates the PDF and sends it in one step, so the caller never
/// has to handle base64 or risk sending a stale document.
api.MapPost("/agreements/{id:guid}/prepare-and-send", async (
    Guid id, AgreementService svc, DocumentService docs, AppDbContext db,
    ZohoSigningService zoho, IConfiguration cfg, CancellationToken ct) =>
{
    var a = await svc.LoadAsync(id, ct);

    // Zoho: stamp paper + agreement, signature boxes on every page, Aadhaar eSign only,
    // second party first. Draft and boxes are free; the submit needs Zoho:ArmSpending.
    if (UsesZoho(a, cfg))
    {
        var zohoOutcome = await zoho.SendAsync(id, ct);
        return OutcomeToResult(zohoOutcome, o => new { zohoRequestId = o.RequestId, requestStatus = o.RequestStatus });
    }

    var doc = docs.BuildAgreementDocument(a);

    if (!doc.Success)
        return Results.BadRequest(new { error = doc.Error });

    a.AgreementPdfPath = doc.Path;
    await db.SaveChangesAsync(ct);

    var outcome = await svc.SendForSigningAsync(id, doc.Base64!, ct);
    return OutcomeToResult(outcome, o => new
    {
        documentId = o.DocumentId,
        signatories = o.Signatories.Select(s => new { s.Name, s.Email, s.SignUrl })
    });
});

// ---------------------------------------------------------------------------
// Zoho signing previews — local only, no Zoho call, nothing spent
// ---------------------------------------------------------------------------

/// The exact file that would be uploaded to Zoho: stamp paper + agreement.
api.MapGet("/agreements/{id:guid}/final-pdf", async (Guid id, ZohoSigningService zoho, CancellationToken ct) =>
{
    try
    {
        var p = await zoho.PreviewAsync(id, drawBoxes: false, ct);
        return Results.File(p.Pdf, "application/pdf", $"final-{id}.pdf");
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

/// The final PDF with every signature box drawn and labelled — check the corners by eye.
api.MapGet("/agreements/{id:guid}/signing-preview", async (Guid id, ZohoSigningService zoho, CancellationToken ct) =>
{
    try
    {
        var p = await zoho.PreviewAsync(id, drawBoxes: true, ct);
        return Results.File(p.Pdf, "application/pdf", $"signing-preview-{id}.pdf");
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

/// The same layout as numbers: pages, sizes, every box in points, and the overlap check.
api.MapGet("/agreements/{id:guid}/signing-layout", async (Guid id, ZohoSigningService zoho, CancellationToken ct) =>
{
    try
    {
        var p = await zoho.PreviewAsync(id, drawBoxes: false, ct);
        return Results.Ok(new
        {
            pageCount = p.Composed.Pages.Count,
            stampPages = p.Composed.StampPageCount,
            sizeBytes = p.Composed.Bytes.Length,
            hasOverlap = p.Report.HasOverlap,
            summary = p.Report.Summary,
            pages = p.Composed.Pages.Select((size, i) => new
            {
                page = i + 1,
                kind = i < p.Composed.StampPageCount ? "stamp" : "agreement",
                width = Math.Round(size.Width, 1),
                height = Math.Round(size.Height, 1),
                boxes = p.Boxes.Where(b => b.PageIndex == i).Select(b => new
                {
                    party = b.Role,
                    x = Math.Round(b.X, 1), y = Math.Round(b.Y, 1),
                    width = b.W, height = b.H
                })
            })
        });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

/// Forget an unsent Zoho draft so the next send uploads a fresh one. Local only — delete
/// the old draft in Zoho Sign by hand. Refused once the request has been submitted.
api.MapPost("/agreements/{id:guid}/zoho/reset-draft", async (Guid id, ZohoSigningService zoho, CancellationToken ct) =>
{
    var refused = await zoho.ResetDraftAsync(id, ct);
    return refused is null
        ? Results.Ok(new { reset = true, note = "Delete the old draft in Zoho Sign, then send again." })
        : Results.Conflict(new { reset = false, error = refused });
});

// ---------------------------------------------------------------------------
// Zoho Sign webhook receiver
//
// Raw body, for the HMAC. 200 for anything authenticated, even if handling fails —
// Zoho disables a webhook after repeated failures. Polling covers a lost event.
// ---------------------------------------------------------------------------
app.MapPost("/webhooks/zoho-sign", async (
    HttpRequest request, ZohoWebhookProcessor processor, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var hookLog = loggerFactory.CreateLogger("Zoho.Webhook");

    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync(ct);

    hookLog.LogInformation("ZOHO WEBHOOK IN from={Ip}\n  body: {Body}",
        request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        string.IsNullOrWhiteSpace(rawBody) ? "(empty)" : rawBody);

    if (!processor.Verify(rawBody,
            request.Headers[ZohoWebhookProcessor.SignatureHeader].ToString(),
            request.Headers[ZohoWebhookProcessor.TimestampHeader].ToString()))
    {
        return Results.Unauthorized();
    }

    try
    {
        var processed = await processor.ProcessAsync(rawBody, ct);
        return Results.Ok(new { received = true, processed });
    }
    catch (Exception ex)
    {
        hookLog.LogError(ex, "Zoho webhook authenticated but handling failed. Polling will catch up.");
        return Results.Ok(new { received = true, processed = false });
    }
});

// ---------------------------------------------------------------------------
// Webhook receiver
//
// The raw body is read deliberately: JSON model binding normalises whitespace,
// which changes the bytes and breaks the HMAC. Return fast — eDrafter times out
// at 10 seconds and retries up to 5 times.
// ---------------------------------------------------------------------------
app.MapPost("/webhooks/edrafter", async (
    HttpRequest request, WebhookProcessor processor, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var hookLog = loggerFactory.CreateLogger("EDrafter.Webhook");

    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync(ct);

    var signature = request.Headers["X-eDrafter-Signature"].ToString();
    var eventName = request.Headers["X-eDrafter-Event"].ToString();
    var correlation = Guid.NewGuid().ToString("N")[..8];

    // Log the delivery in full BEFORE verifying. A rejected signature is exactly the
    // case you need the payload for — logging only after the check would hide it.
    hookLog.LogInformation(
        "WEBHOOK IN  [{Id}] event={Event} from={Ip}\n  signature: {Sig}\n  body: {Body}",
        correlation,
        string.IsNullOrWhiteSpace(eventName) ? "(no header)" : eventName,
        request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        string.IsNullOrWhiteSpace(signature) ? "(none)" : signature,
        string.IsNullOrWhiteSpace(rawBody) ? "(empty)" : rawBody);

    if (!processor.VerifySignature(rawBody, signature))
    {
        hookLog.LogError(
            "WEBHOOK REJECTED [{Id}] HMAC mismatch. Either the secret is wrong, or this " +
            "delivery did not come from eDrafter. Body logged above.", correlation);
        return Results.Unauthorized();
    }

    var processed = await processor.ProcessAsync(rawBody, ct);

    hookLog.LogInformation(
        "WEBHOOK OK  [{Id}] event={Event} signature=verified processed={Processed} duplicate={Duplicate}",
        correlation, eventName, processed, !processed);

    // 200 either way: a duplicate delivery is a success, not an error.
    return Results.Ok(new { received = true, processed, duplicate = !processed });
});

// ---------------------------------------------------------------------------
// Reconciliation — the ONLY recovery path for an UNKNOWN outcome
// ---------------------------------------------------------------------------
api.MapPost("/reconcile", async (Reconciler rec, CancellationToken ct) =>
    Results.Ok(await rec.ReconcileAllAsync(ct)));

/// Force a polling sweep now, rather than waiting for the interval.
api.MapPost("/poll-now", async (PollingService _, IServiceProvider sp, CancellationToken ct) =>
{
    var polling = sp.GetServices<IHostedService>().OfType<PollingService>().FirstOrDefault();
    if (polling is null) return Results.Problem("Polling service not registered");
    await polling.PollOnceAsync(ct);
    return Results.Ok(new { swept = true });
});

app.MapHub<AgreementHub>("/hubs/agreements");

app.Run();

// ---------------------------------------------------------------------------

static object ToDto(Agreement a) => new
{
    id = a.Id,
    refId = a.RefId,
    status = a.Status.ToString(),
    firstParty = new { name = a.FirstPartyName, email = a.FirstPartyEmail, phone = a.FirstPartyPhone },
    secondParty = new { name = a.SecondPartyName, email = a.SecondPartyEmail, phone = a.SecondPartyPhone },
    propertyAddress = a.PropertyAddress,
    considerationAmount = a.ConsiderationAmount,
    monthlyRent = a.MonthlyRent,
    leaseTermMonths = a.LeaseTermMonths,
    leaseStartDate = a.LeaseStartDate,
    denomination = a.Denomination,
    orderIdd = a.OrderIdd,
    orderDisplayId = a.OrderDisplayId,
    certificateNo = a.CertificateNo,
    esignDocumentId = a.EsignDocumentId,
    esignCost = a.EsignCostPaise.HasValue ? a.EsignCostPaise.Value / 100m : (decimal?)null,
    signingProvider = a.SigningProvider,
    zohoRequestId = a.ZohoRequestId,
    zohoSubmittedAt = a.ZohoSubmittedAt,
    sentForSigning = a.EsignDocumentId is not null || a.ZohoSubmittedAt is not null,
    hasSignedPdf = a.SignedPdfPath is not null,
    signatories = a.Signatories
        .OrderBy(s => s.SigningOrder ?? int.MaxValue)
        .Select(s => new
        {
            s.Name, s.Email, s.Phone, s.Status, s.SignUrl, s.SignedAt, s.Role, s.SigningOrder
        }),
    createdAt = a.CreatedAt,
    updatedAt = a.UpdatedAt
};

/// Maps a spend outcome onto an HTTP response, keeping UNKNOWN visibly distinct
/// from failure — the caller must not treat it as "it didn't happen".
static IResult OutcomeToResult<T>(SpendOutcome<T> outcome, Func<T, object> project) where T : class =>
    outcome.Kind switch
    {
        SpendResultKind.Succeeded => Results.Ok(new
        {
            status = "succeeded",
            result = project(outcome.Value!)
        }),

        SpendResultKind.FailedSafe => Results.BadRequest(new
        {
            status = "failed_safe",
            message = outcome.Message,
            note = "Rejected by the provider. No money moved — safe to correct and try again."
        }),

        SpendResultKind.Unknown => Results.Json(new
        {
            status = "unknown",
            message = outcome.Message,
            note = "The outcome is genuinely unknown — money may or may not have moved. " +
                   "Call POST /api/reconcile. Do NOT retry this call.",
            attemptId = outcome.Attempt?.Id
        }, statusCode: StatusCodes.Status409Conflict),

        SpendResultKind.AlreadyAttempted => Results.Json(new
        {
            status = "already_attempted",
            message = outcome.Message,
            note = "The idempotency guard stopped a second charge.",
            attemptId = outcome.Attempt?.Id,
            existingStatus = outcome.Attempt?.Status.ToString()
        }, statusCode: StatusCodes.Status409Conflict),

        SpendResultKind.Blocked => Results.Json(new
        {
            status = "blocked",
            message = outcome.Message
        }, statusCode: StatusCodes.Status403Forbidden),

        _ => Results.Problem("Unhandled spend outcome")
    };

/// Zoho unless the agreement already went (or was set) to eDrafter for signing.
static bool UsesZoho(Agreement a, IConfiguration cfg) =>
    a.EsignDocumentId is null &&
    string.Equals(a.SigningProvider ?? cfg["Signing:Provider"] ?? ZohoSigningService.Provider,
        ZohoSigningService.Provider, StringComparison.OrdinalIgnoreCase);

public sealed record SendForSigningRequest(string DocumentBase64);
