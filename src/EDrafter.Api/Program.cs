using EDrafter.Api.Data;
using EDrafter.Api.EDrafterClient;
using EDrafter.Api.Hubs;
using EDrafter.Api.Services;
using Microsoft.EntityFrameworkCore;

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

builder.Services.AddScoped<SpendGuard>();
builder.Services.AddScoped<SpendLedger>();
builder.Services.AddScoped<DutyCalculator>();
builder.Services.AddScoped<EsignPricing>();
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
}

app.UseCors();

var log = app.Services.GetRequiredService<ILogger<Program>>();
var mode = app.Configuration["EDrafter:Mode"] ?? "Mock";
var armed = app.Configuration.GetValue("EDrafter:ArmSpending", false);
log.LogWarning("eDrafter POC API starting. Mode={Mode} ArmSpending={Armed} BaseUrl={Url}",
    mode, armed, app.Configuration["EDrafter:BaseUrl"]);

var api = app.MapGroup("/api");

// ---------------------------------------------------------------------------
// Health and spend status
// ---------------------------------------------------------------------------
api.MapGet("/health", async (EDrafterApiClient client, SpendGuard guard, CancellationToken ct) =>
{
    var account = await client.GetAccountAsync(ct);
    var summary = await guard.SummaryAsync(ct);
    return Results.Ok(new
    {
        ok = true,
        edrafter = new { account?.Company, account?.Balance },
        spend = summary
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
    var a = await svc.CreateDraftAsync(input, ct);
    return Results.Created($"/api/agreements/{a.Id}", ToDto(a));
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
                consideration = q.Duty.ConsiderationAmount,
                pct = q.Duty.Pct,
                cap = q.Duty.CapRupees,
                amount = q.Duty.Duty,
                wasCapped = q.Duty.WasCapped,
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
            esign = new
            {
                signMethod = q.Esign.SignMethod,
                signatories = q.Esign.SignatoryCount,
                ratePerSignatory = q.Esign.RateRupees,
                total = q.Esign.TotalRupees,
                gst = "not charged on e-sign",
                confirmation = q.Esign.ConfirmationText
            },
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

api.MapPost("/agreements/{id:guid}/refresh-signing", async (Guid id, AgreementService svc, CancellationToken ct) =>
{
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
    Guid id, AgreementService svc, DocumentService docs, AppDbContext db, CancellationToken ct) =>
{
    var a = await svc.LoadAsync(id, ct);
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
    signatories = a.Signatories.Select(s => new
    {
        s.Name, s.Email, s.Phone, s.Status, s.SignUrl, s.SignedAt
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
            note = "Rejected by eDrafter. No money moved — safe to correct and try again."
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

public sealed record SendForSigningRequest(string DocumentBase64);
