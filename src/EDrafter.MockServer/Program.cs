using EDrafter.MockServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<MockState>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHttpClient("webhook").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

var state = app.Services.GetRequiredService<MockState>();
var hooks = app.Services.GetRequiredService<WebhookDispatcher>();
var log = app.Services.GetRequiredService<ILogger<Program>>();

// How long a "Pending -> Completed" order takes in the mock. Real eDrafter is
// 1 working hour to 2 working days, gated behind a human accepting the order.
var orderDelay = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("Mock:OrderCompletionSeconds", 30));

// ---------------------------------------------------------------------------
// Auth: every endpoint requires x-api-key, exactly as the real API does.
// ---------------------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/__mock"))
    {
        await next();
        return;
    }

    if (!ctx.Request.Headers.TryGetValue("x-api-key", out var key) || string.IsNullOrWhiteSpace(key))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing x-api-key header" });
        return;
    }

    await next();
});

var api = app.MapGroup("/api/v1");

// ---------------------------------------------------------------------------
// 1. Account
// ---------------------------------------------------------------------------
api.MapGet("/me", () => Results.Ok(new
{
    name = "POC Test Account",
    email = "poc@example.com",
    company = "eDrafter POC",
    phone = "9876543210",
    gstin = "29AAAAA0000A1Z5",
    balance = state.WalletBalance
}));

// ---------------------------------------------------------------------------
// 2. Catalog
// ---------------------------------------------------------------------------
api.MapGet("/states", () => Results.Ok(new[]
{
    "Karnataka", "Maharashtra", "Delhi", "Gujarat", "Tamil Nadu", "Rajasthan"
}));

api.MapGet("/products", () => Results.Ok(new[]
{
    new { _id = MockState.KarnatakaProductId, state = "Karnataka", serviceable = true },
    new { _id = "664a1b2c3d4e5f6a7b8c9d01", state = "Maharashtra", serviceable = true }
}));

api.MapGet("/products/{id}", (string id) => id == MockState.KarnatakaProductId
    ? Results.Ok(new { _id = id, state = "Karnataka", serviceable = true })
    : Results.NotFound(new { error = "Product not found" }));

api.MapGet("/states/{stateName}/articles", (string stateName) => Results.Ok(new
{
    state = stateName,
    articles = new[]
    {
        new
        {
            code = "30(1)(i)",
            name = "Lease of Immovable Property — Not exceeding 1 year in case of Residential property"
        },
        new { code = "5(J)", name = "Agreement (in any other cases)" },
        new { code = "4", name = "Affidavit" }
    }
}));

// The rules endpoint our form is built from. Note what it does NOT return for
// 30(1)(i): any cap. That absence is the open question S1 with eDrafter.
api.MapGet("/states/{stateName}/rules", (string stateName, string? articleCode) =>
{
    var constraint = articleCode switch
    {
        "30(1)(i)" => new { type = "range", min = 100m, max = (decimal?)null, label = "Lease of Immovable Property — not exceeding 1 year, residential" },
        "5(J)" => new { type = "range", min = 500m, max = (decimal?)null, label = "Agreement (in any other cases)" },
        _ => new { type = "range", min = 100m, max = (decimal?)null, label = "General" }
    };

    return Results.Ok(new
    {
        state = stateName,
        known = true,
        stampType = "e-Stamp paper",
        denomination = new
        {
            type = "article",
            codeRules = new Dictionary<string, object>
            {
                ["30(1)(i)"] = new { min = 100 },
                ["5(J)"] = new { min = 500 },
                ["4"] = new { min = 100 }
            }
        },
        denominationConstraint = constraint,
        denominationHint = $"Minimum ₹{constraint.min:0} (no upper cap)",
        govSurchargePct = 0,
        fields = new
        {
            maxFieldLength = 50,
            disallowSpecialChars = true,
            disallowNumericInNames = false,
            considerationPriceAllowed = true,
            firstPartyAddressRequired = false,
            secondPartyNameRequired = true,
            secondPartyAcceptable = Array.Empty<string>(),
            purchaserOnly = false,
            purchaserOnlyFields = Array.Empty<string>()
        }
    });
});

// ---------------------------------------------------------------------------
// 3. Validation (free)
// ---------------------------------------------------------------------------
api.MapPost("/validate/order", (OrderRequest req) =>
{
    var errors = ValidateOrder(req);
    return errors.Count > 0
        ? Results.Ok(new { valid = false, errors })
        : Results.Ok(new { valid = true, errors = Array.Empty<string>() });
});

// ---------------------------------------------------------------------------
// 4. Pricing (free)
// ---------------------------------------------------------------------------
api.MapPost("/orders/quote", (QuoteRequest req) =>
{
    if (req.Quantity <= 0) return Results.BadRequest(new { error = "quantity must be >= 1" });
    if (req.Denomination <= 0) return Results.BadRequest(new { error = "denomination must be > 0" });

    var q = Pricing.Quote(req.Quantity, req.Denomination, req.DoorstepDelivery ?? false);
    return Results.Ok(new
    {
        state = "Karnataka",
        doorstepDelivery = req.DoorstepDelivery ?? false,
        quantity = req.Quantity,
        denomination = req.Denomination,
        stampValue = q.StampValue,
        serviceCharge = q.ServiceCharge,
        shipping = q.Shipping,
        gst = q.Gst,
        total = q.Total,
        currency = "INR",
        walletBalance = state.WalletBalance,
        affordable = state.WalletBalance >= q.Total
    });
});

// ---------------------------------------------------------------------------
// 5. Orders — POST /orders SPENDS MONEY
// ---------------------------------------------------------------------------
api.MapPost("/orders", async (OrderRequest req) =>
{
    var errors = ValidateOrder(req);
    if (errors.Count > 0)
        return Results.BadRequest(new { error = "Validation failed", errors });

    var q = Pricing.Quote(req.Quantity, req.Denomination, req.DoorstepDelivery ?? false);

    if (!state.TryDebit(q.Total, $"Order {req.RefId ?? "(no ref)"}"))
        return Results.Json(
            new { error = "Insufficient wallet balance", required = q.Total, balance = state.WalletBalance },
            statusCode: StatusCodes.Status402PaymentRequired);

    var order = new MockOrder
    {
        Idd = state.NextOrderIdd(),
        DisplayId = state.NextDisplayId(),
        RefId = req.RefId,
        FirstParty = req.FirstParty!,
        SecondParty = req.SecondParty!,
        ArticleCode = req.ArticleCode,
        Purpose = req.Purpose,
        Quantity = req.Quantity,
        Denomination = req.Denomination,
        ConsiderationPrice = req.ConsiderationPrice,
        TotalAmount = q.Total,
        DoorstepDelivery = req.DoorstepDelivery ?? false
    };
    state.Orders[order.Idd] = order;

    log.LogWarning("MOCK SPEND: order {Idd} debited ₹{Total}. Balance now ₹{Balance}",
        order.Idd, q.Total, state.WalletBalance);

    // Simulate the async wait, compressed from ~1 working day to seconds.
    _ = Task.Run(async () =>
    {
        await Task.Delay(orderDelay);
        order.Status = "Processing";
        await Task.Delay(orderDelay);

        for (var i = 1; i <= order.Quantity; i++)
        {
            order.Stamps.Add(new MockStamp
            {
                StampId = i,
                CertificateNo = $"IN-KA{Random.Shared.Next(10000000, 99999999)}{i}Y",
                Denomination = order.Denomination,
                State = "Karnataka"
            });
        }
        order.Status = "Completed";

        await hooks.FireAsync("order.completed", new
        {
            orderId = order.Idd,
            status = order.Status,
            quantity = order.Quantity,
            denomination = order.Denomination,
            total = order.TotalAmount,
            stampCount = order.Stamps.Count,
            completedAt = DateTime.UtcNow
        });
    });

    return Results.Created($"/api/v1/orders/{order.Idd}", new
    {
        message = "Order created successfully",
        orderId = order.Idd,
        reference = order.DisplayId,
        refId = order.RefId,
        considerationPrice = order.ConsiderationPrice,
        doorstepDelivery = order.DoorstepDelivery,
        shipping = q.Shipping,
        totalAmount = q.Total,
        order = new { _idd = order.Idd, displayId = order.DisplayId, status = order.Status }
    });
});

api.MapGet("/orders", () => Results.Ok(new
{
    count = state.Orders.Count,
    orders = state.Orders.Values
        .OrderByDescending(o => o.CreatedAt)
        .Select(ToOrderDto)
}));

api.MapGet("/orders/{idd:int}", (int idd) => state.Orders.TryGetValue(idd, out var o)
    ? Results.Ok(ToOrderDto(o))
    : Results.NotFound(new { error = "Order not found" }));

// The recovery handle for an UNKNOWN outcome: look the order up by OUR reference
// instead of retrying the spend.
api.MapGet("/orders/by-ref/{refId}", (string refId) =>
{
    var matches = state.Orders.Values
        .Where(o => string.Equals(o.RefId, refId, StringComparison.Ordinal))
        .OrderByDescending(o => o.CreatedAt)
        .ToList();

    return matches.Count == 0
        ? Results.NotFound(new { error = "No order with that refId" })
        : Results.Ok(new { count = matches.Count, orders = matches.Select(ToOrderDto) });
});

api.MapPut("/orders/{idd:int}/cancel", (int idd) =>
{
    if (!state.Orders.TryGetValue(idd, out var order))
        return Results.NotFound(new { error = "Order not found" });

    if (order.Status != "Pending")
        return Results.Conflict(new { error = $"Cannot cancel an order in status {order.Status}" });

    order.Status = "Cancelled";
    state.Credit(order.TotalAmount, $"Refund for cancelled order {order.Idd}");

    return Results.Ok(new
    {
        message = "Order cancelled",
        orderId = order.Idd,
        refund = new { walletRefunded = order.TotalAmount, currency = "INR", externalRefundDue = false }
    });
});

// ---------------------------------------------------------------------------
// 6. Stamps
// ---------------------------------------------------------------------------
api.MapGet("/orders/{idd:int}/stamps", (int idd) =>
{
    if (!state.Orders.TryGetValue(idd, out var order))
        return Results.NotFound(new { error = "Order not found" });

    return Results.Ok(new
    {
        orderId = order.Idd,
        status = order.Status,
        count = order.Stamps.Count,
        stamps = order.Stamps.Select(s => new
        {
            stampId = s.StampId,
            certificateNo = s.CertificateNo,
            denomination = s.Denomination,
            downloadUrl = $"/api/v1/orders/{order.Idd}/stamps/{s.StampId}/download"
        })
    });
});

api.MapGet("/orders/{idd:int}/stamps/{stampId:int}/download", (int idd, int stampId) =>
{
    if (!state.Orders.TryGetValue(idd, out var order))
        return Results.NotFound(new { error = "Order not found" });

    var stamp = order.Stamps.FirstOrDefault(s => s.StampId == stampId);
    if (stamp is null) return Results.NotFound(new { error = "Stamp not found" });

    stamp.Downloaded = true; // the real API marks it Used on download
    var pdf = FakePdf.Build($"e-STAMP CERTIFICATE\n{stamp.CertificateNo}\nKarnataka  Rs.{stamp.Denomination}");
    return Results.File(pdf, "application/pdf", $"stamp-{stamp.CertificateNo}.pdf");
});

api.MapGet("/stamps", (string? refId) =>
{
    var stamps = state.Orders.Values
        .Where(o => refId is null || o.RefId == refId)
        .SelectMany(o => o.Stamps.Where(s => !s.IsConsumed).Select(s => new
        {
            orderId = o.Idd,
            refId = o.RefId,
            stampId = s.StampId,
            certificateNo = s.CertificateNo,
            denomination = s.Denomination,
            state = s.State
        }))
        .ToList();

    return Results.Ok(new { count = stamps.Count, stamps });
});

// ---------------------------------------------------------------------------
// 7. E-Signing — POST /esign SPENDS MONEY, at link generation, per signatory
// ---------------------------------------------------------------------------
api.MapPost("/esign", async (EsignRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "name is required" });
    if (req.Signatories is null || req.Signatories.Count == 0)
        return Results.BadRequest(new { error = "at least one signatory is required" });

    var validMethods = new[] { "phone_otp", "aadhaar_otp", "dsc" };
    if (!validMethods.Contains(req.SignMethod))
        return Results.BadRequest(new { error = $"signMethod must be one of: {string.Join(", ", validMethods)}" });

    // phone is required per signatory when signing by phone OTP
    if (req.SignMethod == "phone_otp" && req.Signatories.Any(s => string.IsNullOrWhiteSpace(s.Phone)))
        return Results.BadRequest(new { error = "phone is required for every signatory when signMethod is phone_otp" });

    // Reject a stamp that has already been consumed - the real API 409s here.
    if (req.OrderId is { } oid && req.StampId is { } sid)
    {
        if (!state.Orders.TryGetValue(oid, out var order))
            return Results.NotFound(new { error = "Order not found" });

        var stamp = order.Stamps.FirstOrDefault(s => s.StampId == sid);
        if (stamp is null) return Results.NotFound(new { error = "Stamp not found" });
        if (stamp.IsConsumed) return Results.Conflict(new { error = "Stamp already consumed" });

        stamp.IsConsumed = true;
    }

    var cost = Pricing.EsignCost(req.SignMethod!, req.Signatories.Count);
    if (!state.TryDebit(cost, $"e-Sign '{req.Name}' x{req.Signatories.Count} ({req.SignMethod})"))
        return Results.Json(
            new { error = "Insufficient wallet balance", required = cost, balance = state.WalletBalance },
            statusCode: StatusCodes.Status402PaymentRequired);

    var documentId = Guid.NewGuid().ToString("N");
    var doc = new MockEsignDoc
    {
        DocumentId = documentId,
        Uid = $"EDR{Random.Shared.Next(1000, 9999)}",
        Name = req.Name!,
        SignMethod = req.SignMethod!,
        ChargedAmount = cost,
        ExpiresAt = DateTime.UtcNow.AddDays(req.ExpiryDays ?? 7)
    };

    foreach (var s in req.Signatories)
    {
        doc.Signatories.Add(new MockSignatory
        {
            Name = s.Name,
            Email = s.Email,
            Phone = s.Phone,
            SignUrl = $"http://localhost:5099/sign/{documentId}/{Uri.EscapeDataString(s.Email)}"
        });
    }

    state.EsignDocs[documentId] = doc;

    log.LogWarning("MOCK SPEND: e-sign '{Name}' debited ₹{Cost} for {N} signatories. Balance now ₹{Balance}",
        doc.Name, cost, doc.Signatories.Count, state.WalletBalance);

    return Results.Created($"/api/v1/esign/{documentId}", new
    {
        documentId,
        uid = doc.Uid,
        name = doc.Name,
        status = doc.Status,
        signatories = doc.Signatories.Select(s => new
        {
            name = s.Name,
            email = s.Email,
            status = s.Status,
            signUrl = s.SignUrl
        })
    });
});

api.MapGet("/esign", () => Results.Ok(new
{
    count = state.EsignDocs.Count,
    documents = state.EsignDocs.Values
        .OrderByDescending(d => d.CreatedAt)
        .Select(d => new
        {
            documentId = d.DocumentId,
            name = d.Name,
            status = d.Status,
            signatoryCount = d.Signatories.Count,
            signedCount = d.Signatories.Count(s => s.Status == "signed"),
            signedUrl = d.SignedUrl,
            createdAt = d.CreatedAt
        })
}));

api.MapGet("/esign/{id}", (string id) => state.EsignDocs.TryGetValue(id, out var d)
    ? Results.Ok(new
    {
        documentId = d.DocumentId,
        name = d.Name,
        status = d.Status,
        signatories = d.Signatories.Select(s => new
        {
            name = s.Name,
            email = s.Email,
            status = s.Status,
            signedAt = s.SignedAt
        })
    })
    : Results.NotFound(new { error = "Document not found" }));

api.MapGet("/esign/{id}/signed", (string id) =>
{
    if (!state.EsignDocs.TryGetValue(id, out var d))
        return Results.NotFound(new { error = "Document not found" });
    if (d.Status != "completed")
        return Results.Conflict(new { error = $"Document is {d.Status}, not completed" });

    var pdf = FakePdf.Build($"SIGNED AGREEMENT\n{d.Name}\nAll {d.Signatories.Count} parties signed.");
    return Results.File(pdf, "application/pdf", $"signed-{d.DocumentId}.pdf");
});

// ---------------------------------------------------------------------------
// 8. Wallet
// ---------------------------------------------------------------------------
api.MapGet("/transactions", (string? type) =>
{
    var txns = state.Transactions
        .Where(t => type is null || t.Type == type)
        .OrderByDescending(t => t.At)
        .ToList();

    return Results.Ok(new
    {
        summary = new
        {
            totalCredit = state.Transactions.Where(t => t.Type == "Credit").Sum(t => t.Amount),
            totalDebit = state.Transactions.Where(t => t.Type == "Debit").Sum(t => t.Amount),
            balance = state.WalletBalance
        },
        transactions = txns.Select(t => new
        {
            type = t.Type,
            amount = t.Amount,
            description = t.Description,
            at = t.At
        })
    });
});

// ---------------------------------------------------------------------------
// 9. Webhooks
// ---------------------------------------------------------------------------
api.MapPost("/webhooks", (WebhookRequest req) =>
{
    var hook = new MockWebhook
    {
        Id = Guid.NewGuid().ToString("N")[..24],
        Url = req.Url,
        Events = req.Events ?? ["order.completed", "esign.completed"],
        Secret = "whsec_" + Guid.NewGuid().ToString("N")
    };
    state.Webhooks[hook.Id] = hook;

    return Results.Created($"/api/v1/webhooks/{hook.Id}", new
    {
        message = "Webhook registered",
        webhook = new { _id = hook.Id, url = hook.Url, events = hook.Events, secret = hook.Secret, active = hook.Active }
    });
});

api.MapGet("/webhooks", () => Results.Ok(new
{
    supportedEvents = new[] { "order.completed", "esign.completed" },
    webhooks = state.Webhooks.Values.Select(w => new { _id = w.Id, url = w.Url, events = w.Events, active = w.Active })
}));

api.MapDelete("/webhooks/{id}", (string id) => state.Webhooks.TryRemove(id, out _)
    ? Results.Ok(new { message = "Webhook deleted" })
    : Results.NotFound(new { error = "Webhook not found" }));

// ---------------------------------------------------------------------------
// Mock-only controls (no auth) — drive the simulation from tests or curl.
// ---------------------------------------------------------------------------
var mock = app.MapGroup("/__mock");

mock.MapGet("/state", () => Results.Ok(new
{
    walletBalance = state.WalletBalance,
    orders = state.Orders.Count,
    esignDocs = state.EsignDocs.Count,
    webhooks = state.Webhooks.Count
}));

// Force an order straight to Completed instead of waiting for the timer.
mock.MapPost("/orders/{idd:int}/complete", async (int idd) =>
{
    if (!state.Orders.TryGetValue(idd, out var order))
        return Results.NotFound(new { error = "Order not found" });

    if (order.Stamps.Count == 0)
    {
        for (var i = 1; i <= order.Quantity; i++)
        {
            order.Stamps.Add(new MockStamp
            {
                StampId = i,
                CertificateNo = $"IN-KA{Random.Shared.Next(10000000, 99999999)}{i}Y",
                Denomination = order.Denomination
            });
        }
    }
    order.Status = "Completed";

    await hooks.FireAsync("order.completed", new
    {
        orderId = order.Idd,
        status = order.Status,
        quantity = order.Quantity,
        denomination = order.Denomination,
        total = order.TotalAmount,
        stampCount = order.Stamps.Count,
        completedAt = DateTime.UtcNow
    });

    return Results.Ok(new { message = "Order forced to Completed", orderId = order.Idd });
});

// Make every signatory sign, and fire esign.completed.
mock.MapPost("/esign/{id}/sign-all", async (string id) =>
{
    if (!state.EsignDocs.TryGetValue(id, out var doc))
        return Results.NotFound(new { error = "Document not found" });

    foreach (var s in doc.Signatories)
    {
        s.Status = "signed";
        s.SignedAt = DateTime.UtcNow;
    }
    doc.Status = "completed";
    doc.SignedUrl = $"/api/v1/esign/{doc.DocumentId}/signed";

    await hooks.FireAsync("esign.completed", new
    {
        documentId = doc.DocumentId,
        name = doc.Name,
        status = doc.Status,
        signedUrl = doc.SignedUrl
    });

    return Results.Ok(new { message = "All signatories signed", documentId = doc.DocumentId });
});

// Simulate a signatory declining — there is NO webhook for this in the real API,
// which is exactly why our polling path has to cover it.
mock.MapPost("/esign/{id}/decline", (string id, string email) =>
{
    if (!state.EsignDocs.TryGetValue(id, out var doc))
        return Results.NotFound(new { error = "Document not found" });

    var sig = doc.Signatories.FirstOrDefault(s => s.Email == email);
    if (sig is null) return Results.NotFound(new { error = "Signatory not found" });

    sig.Status = "declined";
    doc.Status = "declined";

    return Results.Ok(new
    {
        message = "Signatory declined. No refund — the charge stands.",
        documentId = doc.DocumentId,
        chargedAmount = doc.ChargedAmount
    });
});

mock.MapPost("/reset", () =>
{
    state.Orders.Clear();
    state.EsignDocs.Clear();
    state.WalletBalance = 5_000m;
    return Results.Ok(new { message = "Mock state reset" });
});

log.LogInformation("eDrafter MOCK server listening. Order completion delay: {Delay}s", orderDelay.TotalSeconds);
app.Run();

// ---------------------------------------------------------------------------

static List<string> ValidateOrder(OrderRequest req)
{
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(req.FirstParty)) errors.Add("firstParty is required");
    if (string.IsNullOrWhiteSpace(req.SecondParty)) errors.Add("secondParty is required");
    if (string.IsNullOrWhiteSpace(req.PurchasedBy)) errors.Add("purchasedBy is required");
    if (string.IsNullOrWhiteSpace(req.ProductId)) errors.Add("product_id is required");
    if (string.IsNullOrWhiteSpace(req.Purpose)) errors.Add("purpose is required");
    if (req.Quantity <= 0) errors.Add("quantity must be >= 1");
    if (req.Denomination <= 0) errors.Add("denomination must be > 0");

    // dutyPaidBy must exactly equal firstParty or secondParty
    if (string.IsNullOrWhiteSpace(req.DutyPaidBy))
        errors.Add("dutyPaidBy is required");
    else if (req.DutyPaidBy != req.FirstParty && req.DutyPaidBy != req.SecondParty)
        errors.Add("dutyPaidBy must exactly match firstParty or secondParty");

    // 50-char cap and no special characters in names, per the rules endpoint
    foreach (var (label, value) in new[]
             {
                 ("firstParty", req.FirstParty),
                 ("secondParty", req.SecondParty),
                 ("purchasedBy", req.PurchasedBy)
             })
    {
        if (string.IsNullOrWhiteSpace(value)) continue;
        if (value.Length > 50) errors.Add($"{label} exceeds 50 characters");
        if (value.Any(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '.' && c != '-'))
            errors.Add($"{label} contains disallowed special characters");
    }

    return errors;
}

static object ToOrderDto(MockOrder o) => new
{
    _idd = o.Idd,
    displayId = o.DisplayId,
    refId = o.RefId,
    status = o.Status,
    firstParty = o.FirstParty,
    secondParty = o.SecondParty,
    articleCode = o.ArticleCode,
    quantity = o.Quantity,
    denomination = o.Denomination,
    considerationPrice = o.ConsiderationPrice,
    totalAmount = o.TotalAmount,
    doorstepDelivery = o.DoorstepDelivery,
    createdAt = o.CreatedAt,
    stampCount = o.Stamps.Count
};
