# eDrafter B2B API — Endpoint-by-Endpoint Examples

**Purpose:** every endpoint with a copy-pasteable request and a real response shape, in the
order you will actually call them while building. This is the *how*; `06-API-REFERENCE.md`
is the *what and why*.

| | |
|---|---|
| **Base URL** | `https://edrafterb2b.in/api/v1` |
| **Auth** | header `x-api-key: <YOUR_API_KEY>` on **every** request |
| **Content type** | `Content-Type: application/json` on every POST/PUT |
| **Environments** | **Production only — no sandbox.** `POST /orders` spends real money. |
| **Endpoints** | 27 |

> **Read before you call anything:** only three calls touch money — `POST /orders` (debits the
> wallet), `PUT /orders/:idd/cancel` (refunds it), and `POST /esign` (cost unconfirmed).
> Everything else is free. `POST /validate/order` and `POST /orders/quote` are free dry-runs —
> lean on them.

Set these once in your shell so every example below runs as-is:

```bash
export KEY="your_api_key_here"
export BASE="https://edrafterb2b.in/api/v1"
```

---

## Contents

1. [Account](#1-account) — `/me`
2. [Catalog](#2-catalog) — states, products, articles, rules
3. [Validation](#3-validation) — dry-run field check
4. [Pricing](#4-pricing) — quote
5. [Orders](#5-orders) — create, list, look up, cancel
6. [Stamps](#6-stamps) — download, share links
7. [E-Signing](#7-e-signing) — send, track, download signed
8. [Wallet](#8-wallet) — top-ups, ledger
9. [Webhooks](#9-webhooks) — register, verify, handle
10. [End-to-end walkthrough](#10-end-to-end-walkthrough) — one document, start to finish
11. [Error responses](#11-error-responses)

---

# 1. Account

## `GET /me` — account info

Your first call. A 200 means the key works.

```bash
curl -H "x-api-key: $KEY" "$BASE/me"
```

**Response 200**
```json
{
  "name": "Your Name",
  "email": "you@company.com",
  "company": "Your Company",
  "phone": "9876543210",
  "gstin": "22AAAAA0000A1Z5",
  "balance": 50000
}
```

**Use it for:** connectivity check at startup, and **polling the wallet balance**. There is no
low-balance webhook — this is the only way to see the wallet running dry before an order fails
with a `402`.

---

# 2. Catalog

Call these to build the order form. Everything here is free and cacheable.

## `GET /states` — serviceable states

```bash
curl -H "x-api-key: $KEY" "$BASE/states"
```

**Response 200**
```json
["Maharashtra", "Karnataka", "Delhi", "Gujarat", "Tamil Nadu"]
```

⚠️ **Assam and Bihar are not serviceable.** Roughly 26 effective regions — do not promise
"PAN India" in the UI. Drive the state dropdown from this call, never from a hardcoded list.

## `GET /products` — serviceable products

```bash
curl -H "x-api-key: $KEY" "$BASE/products"
```

**Response 200**
```json
[
  { "_id": "664a1b2c3d4e5f6a7b8c9d01", "state": "Maharashtra", "serviceable": true },
  { "_id": "664a1b2c3d4e5f6a7b8c9d02", "state": "Karnataka",   "serviceable": true }
]
```

The `_id` here is the `product_id` every order and quote needs. Map state → `_id` once at
startup and cache it.

## `GET /products/:id` — one product

```bash
curl -H "x-api-key: $KEY" "$BASE/products/664a1b2c3d4e5f6a7b8c9d01"
```

**Response 200** — same shape, single object.
```json
{ "_id": "664a1b2c3d4e5f6a7b8c9d01", "state": "Maharashtra", "serviceable": true }
```

## `GET /states/:state/articles` — document types for a state

Lets the user pick *what kind of document* this is. Pass the chosen `code` to `POST /orders`
as `articleCode`.

```bash
curl -H "x-api-key: $KEY" "$BASE/states/Delhi/articles"
```

**Response 200**
```json
{
  "state": "Delhi",
  "known": true,
  "hasArticleCode": true,
  "count": 98,
  "articles": [
    { "code": "12", "name": "Award" },
    { "code": "23", "name": "Conveyance" },
    { "code": "35", "name": "Lease" }
  ]
}
```

| Flag | Meaning |
|---|---|
| `hasArticleCode: false` | That state's articles carry no codes — send `article` (the name) instead |
| `known: false` | Generic fallback list; the state isn't on file yet |

Delhi alone has **98** article types, so render this as a searchable dropdown, not a plain
`<select>`.

## `GET /states/:state/rules` — validation rules ⭐

**The most useful endpoint in the catalog.** Everything needed to build a correct, state-aware
order form.

```bash
curl -H "x-api-key: $KEY" "$BASE/states/Karnataka/rules"
```

For article-aware states (Karnataka, Gujarat, Rajasthan) pass the article to resolve the exact
denomination limit:

```bash
curl -H "x-api-key: $KEY" "$BASE/states/Karnataka/rules?articleCode=5(J)"
```

**Response 200**
```json
{
  "state": "Karnataka",
  "known": true,
  "stampType": "e-Stamp paper",
  "denomination": {
    "type": "article",
    "codeRules": { "5(J)": { "min": 500 }, "4": { "min": 100 } }
  },
  "denominationConstraint": {
    "type": "range", "min": 500, "max": null,
    "label": "Agreement (in any other cases)"
  },
  "denominationHint": "Minimum ₹500 (no upper cap)",
  "govSurchargePct": 0,
  "fields": {
    "maxFieldLength": 50,
    "disallowSpecialChars": true,
    "disallowNumericInNames": false,
    "considerationPriceAllowed": true,
    "firstPartyAddressRequired": false,
    "secondPartyNameRequired": true,
    "secondPartyAcceptable": [],
    "purchaserOnly": false,
    "purchaserOnlyFields": []
  }
}
```

| Field | Use it for |
|---|---|
| `denominationHint` | Display string — put it straight under the denomination input |
| `denominationConstraint` | `min`/`max` for the numeric input's own validation |
| `govSurchargePct` | Government surcharge — factor into any cost you show |
| `maxFieldLength` | `maxlength` on name inputs (50 in most states) |
| `disallowSpecialChars` | Client-side regex on name fields |
| `secondPartyNameRequired` | Whether to mark second party required |
| `purchaserOnly` | **True for Telangana, MP, West Bengal, Odisha** — render a different form entirely |

> **Build the form from this response. Do not hardcode state rules** — they drift, and this
> endpoint is the same source the eDrafter order form itself uses.

---

# 3. Validation

## `POST /validate/order` — dry-run field check ⭐

**Creates nothing, prices nothing, costs nothing.** Returns a plain-English message per field.
Call it on blur and again on submit.

```bash
curl -X POST "$BASE/validate/order" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "state": "Maharashtra",
    "firstParty": "Ramesh Kumar",
    "secondParty": "",
    "firstPartyAddress": "12 MG Road, Pune",
    "denomination": 100
  }'
```

Send only the fields you have. Pass **`state` or `productId`**, not both.

**Response 200** — note that invalid input still returns 200. Check the `valid` flag, not the
status code.
```json
{
  "valid": false,
  "state": "Maharashtra",
  "stampType": "Stamp paper",
  "rulesApplied": {
    "maxFieldLength": 50,
    "specialCharactersRestricted": true,
    "secondPartyNameRequired": true,
    "firstPartyAddressRequired": true,
    "denominationRule": "Allowed: ₹500"
  },
  "fields": {
    "firstParty":   { "ok": true,  "message": "", "value": "Ramesh Kumar" },
    "secondParty":  { "ok": false, "message": "Second-party name is required for Maharashtra." },
    "denomination": { "ok": false, "message": "Denomination ₹100 is not valid for Maharashtra. Allowed: ₹500." }
  },
  "errors": [
    "Second-party name is required for Maharashtra.",
    "Denomination ₹100 is not valid for Maharashtra. Allowed: ₹500."
  ]
}
```

**Accepted fields:** `state` | `productId`, `firstParty`, `secondParty`, `firstPartyAddress`,
`secondPartyAddress`, `purchaserName`, `fatherName`, `address`, `contact`, `article`,
`articleCode`, `denomination`, `considerationPrice`.

- `valid` is true **only** when there are zero problems
- `fields` gives per-field `{ ok, message }` → bind straight to form control errors
- `errors` is the same thing flattened, for a summary banner

---

# 4. Pricing

## `POST /orders/quote` — price estimate, free

Full cost breakdown **without placing an order or touching the wallet**.

```bash
curl -X POST "$BASE/orders/quote" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "product_id": "664a1b2c3d4e5f6a7b8c9d01",
    "quantity": 10,
    "denomination": 100,
    "doorstepDelivery": false
  }'
```

| Field | Type | Req | Notes |
|---|---|---|---|
| `product_id` | string | ✅ | From `/products` |
| `quantity` | number | ✅ | Whole number |
| `denomination` | number | ✅ | Value **per stamp paper**, in ₹ |
| `doorstepDelivery` | boolean | — | **`true` ⇒ shipping charged; `false`/omitted ⇒ shipping 0** |

**Response 200**
```json
{
  "state": "Maharashtra",
  "doorstepDelivery": false,
  "quantity": 10,
  "denomination": 100,
  "stampValue": 1000,
  "serviceCharge": 500,
  "shipping": 0,
  "gst": 90,
  "total": 1590,
  "currency": "INR",
  "walletBalance": 50000,
  "affordable": true
}
```

**How the total is composed:**

```
stampValue = quantity × denomination            = 10 × 100 = 1000
gst        = 18% × (serviceCharge + shipping)   = 18% × 500 = 90
total      = stampValue + serviceCharge + shipping + gst    = 1590
```

⚠️ **GST is charged on the service charge and shipping only — never on the stamp duty itself.**

`affordable` tells you up front whether the wallet covers it. Check it and block checkout
rather than letting `POST /orders` fail with a `402`.

> **Free and unlimited.** Use it to settle pricing questions empirically — e.g. quote at
> denomination 1000 vs 1001 to see threshold behaviour on your own account.

---

# 5. Orders

## `POST /orders` — create an order 💸

**This spends real money.** The total is auto-calculated and debited from your wallet.

```bash
curl -X POST "$BASE/orders" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "firstParty": "Ramesh Kumar",
    "secondParty": "Jane Smith",
    "purchasedBy": "Ramesh Kumar",
    "dutyPaidBy": "Ramesh Kumar",
    "product_id": "664a1b2c3d4e5f6a7b8c9d01",
    "purpose": "Sale Agreement",
    "articleCode": "23",
    "quantity": 10,
    "denomination": 100,
    "considerationPrice": 50000,
    "doorstepDelivery": false,
    "refId": "REF-2026-88231"
  }'
```

| Field | Type | Req | Notes |
|---|---|---|---|
| `firstParty` | string | ✅ | |
| `secondParty` | string | ✅ | |
| `purchasedBy` | string | ✅ | |
| `dutyPaidBy` | string | ✅ | **Must exactly equal `firstParty` or `secondParty`** — use a dropdown, not free text |
| `product_id` | string | ✅ | From `/products` |
| `purpose` | string | ✅ | Purpose / article type |
| `quantity` | number | ✅ | |
| `denomination` | number | ✅ | Per stamp paper |
| `doorstepDelivery` | boolean | — | **`false`/omitted ⇒ shipping ₹0** — the single biggest cost lever |
| `deliveryAddress` | object \| string | if doorstep | See below. Legacy `address` still accepted |
| `articleCode` | string | — | From `/states/:state/articles` |
| `considerationPrice` | number | — | Transaction value. **Rejected in states that don't allow it** |
| `refId` | string | — | **Your own reference.** ≤120 chars, not unique, aliases `ref_id` / `reference_id` |

**`deliveryAddress` — only when `doorstepDelivery: true`**
```json
{
  "doorstepDelivery": true,
  "deliveryAddress": {
    "name": "Rajesh Kumar",
    "phone": "9876543210",
    "altPhone": "9876543211",
    "addressLine1": "Flat 4B, Green Residency",
    "addressLine2": "Indiranagar",
    "city": "Bengaluru",
    "state": "Karnataka",
    "pincode": "560001"
  }
}
```

**Response 201**
```json
{
  "message": "Order created successfully",
  "orderId": 100042,
  "reference": "MSB001",
  "refId": "REF-2026-88231",
  "considerationPrice": 50000,
  "doorstepDelivery": false,
  "shipping": 0,
  "totalAmount": 1590,
  "order": {
    "_idd": 100042,
    "displayId": "MSB001",
    "status": "Pending"
  }
}
```

> ### ⚠️ Three things to get right here
>
> 1. **`_idd`, not `_id`.** Orders are addressed by *display ID* everywhere downstream. Model
>    it explicitly and never confuse it with a Mongo `_id`.
> 2. **Never auto-retry this call.** It is **not idempotent** — a retry on timeout may place a
>    second order and double-charge. On timeout, look the order up by `refId` instead.
> 3. **Always send your own `refId`.** It is how you recover from exactly that timeout, and how
>    you reconcile later.

**Status progression:** `Pending` → `Processing` → `Completed`, plus `Cancelled`.
Expect **1 hour to 2 working days** before stamps are ready. Build for async.

## `GET /orders` — list orders

Newest first.

```bash
curl -H "x-api-key: $KEY" \
  "$BASE/orders?status=Completed&from=2026-01-01&to=2026-12-31&limit=50"
```

| Param | Notes |
|---|---|
| `status` | `Pending` \| `Processing` \| `Completed` \| `Cancelled` |
| `from` / `to` | `YYYY-MM-DD` |
| `limit` | Default 200, max 1000 |
| `refId` | Exact but **case-insensitive**; returns every match if a refId was reused |

**Response 200**
```json
{
  "count": 2,
  "orders": [
    { "_idd": 100042, "displayId": "MSB001", "refId": "REF-2026-88231",
      "status": "Completed", "quantity": 10, "denomination": 100, "totalAmount": 1590 }
  ]
}
```

## `GET /orders/:idd` — one order by display ID

```bash
curl -H "x-api-key: $KEY" "$BASE/orders/100042"
```

## `GET /orders/by-ref/:refId` — look up by *your* reference

Your timeout-recovery path.

```bash
curl -H "x-api-key: $KEY" "$BASE/orders/by-ref/REF-2026-88231"
```

**Response 200**
```json
{
  "_idd": 100042,
  "refId": "REF-2026-88231",
  "status": "Completed",
  "found": true,
  "matchCount": 1,
  "otherMatchOrderIds": [],
  "stampsUploaded": [
    { "id": 1, "certificateNo": "IN-MH12345678901234A", "used": true, "downloadUrl": "..." }
  ]
}
```

If a refId was reused, the **newest** order is returned and `matchCount` /
`otherMatchOrderIds` list the rest. `404` if nothing matches.

## `PUT /orders/:idd/cancel` — cancel and refund ⭐

```bash
curl -X PUT -H "x-api-key: $KEY" "$BASE/orders/100042/cancel"
```

**Response 200**
```json
{
  "message": "Order cancelled",
  "orderId": 100042,
  "status": "Cancelled",
  "cancelledAt": "2026-06-22T09:30:00.000Z",
  "refund": { "walletRefunded": 1590, "currency": "INR", "externalRefundDue": false }
}
```

⚠️ **Only while status is `Pending`.** Once `Processing`, this fails — you must contact
eDrafter manually. The wallet is refunded automatically.

---

# 6. Stamps

## `GET /orders/:idd/stamps` — stamp download links

```bash
curl -H "x-api-key: $KEY" "$BASE/orders/100042/stamps"
```

**Response 200**
```json
{
  "orderId": 100042,
  "refId": "REF-2026-88231",
  "status": "Completed",
  "stampCount": 2,
  "usedCount": 1,
  "stamps": [
    {
      "id": 1,
      "certificateNo": "IN-MH12345678901234A",
      "downloadUrl": "https://edrafterb2b.in/api/v1/orders/100042/stamps/1/download?api_key=...",
      "used": true,
      "markedVia": "api",
      "downloadedAt": "2026-07-28T09:12:00.000Z"
    },
    { "id": 2, "certificateNo": "IN-MH12345678901234B", "downloadUrl": "...", "used": false }
  ]
}
```

> ⚠️ **`downloadUrl` embeds your API key in the query string.** Never log it, never render it
> in the browser, never email it. Use `POST .../link` below to share a stamp.

## `GET /orders/:idd/stamps/:stampId/download` — download the PDF (tracked)

Returns the **PDF bytes**, not JSON.

```bash
curl -H "x-api-key: $KEY" \
  "$BASE/orders/100042/stamps/1/download" \
  -o stamp-100042-1.pdf
```

> ⚠️ **Fetching this marks the stamp "Used"** in the eDrafter dashboard and stock reports.
> Downloading consumes it — not e-signing it. Download once, deliberately, and store the PDF
> your side.
>
> - Counted **only once the file is fully delivered** — an aborted transfer records nothing
> - Re-downloading keeps the original timestamp, so counts never inflate

## `POST /orders/:idd/stamps/:stampId/link` — self-expiring share link ⭐

A time-limited link that needs **no API key** — safe to hand to an end user or open in a
browser.

```bash
curl -X POST "$BASE/orders/100042/stamps/1/link" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{ "ttlMinutes": 30 }'
```

| Field | Type | Req | Notes |
|---|---|---|---|
| `ttlMinutes` | number | — | Default 30; allowed 1–1440 (clamped) |

**Response 200**
```json
{
  "url": "https://edrafterb2b.in/api/v1/dl/66c1f0e2a1b2c3d4e5f6a7b8.o8s2Xy",
  "ttlMinutes": 30,
  "startsOnFirstOpen": true,
  "note": "This link expires 30 minute(s) after it is first opened."
}
```

- **The clock starts on FIRST OPEN**, not at creation
- Once opened it is valid for `ttlMinutes` **for anyone holding it** — treat it as a bearer token
- Unopened links are cleaned up within 24 hours
- The stamp file itself is never deleted — only the link dies
- Additive: the API-key link keeps working

**This is how you expose a stamp to an end user without leaking the API key.**

## `GET /dl/:token` — open a self-expiring link

No API key — the token *is* the credential.

```bash
curl "$BASE/dl/66c1f0e2a1b2c3d4e5f6a7b8.o8s2Xy" -o stamp.pdf
```

Returns the PDF, or **`410 Gone`** once expired.

## `GET /stamps` — all your ready, unconsumed stamps

Across every order — this is what backs a "pick a stamp" UI.

```bash
curl -H "x-api-key: $KEY" "$BASE/stamps?refId=REF-2026-88231"
```

**Response 200**
```json
{
  "count": 28,
  "stamps": [
    { "orderId": 100063, "refId": "REF-2026-88231", "stampId": 1,
      "certificateNo": "IN-GJ66123456789012Y", "denomination": 300, "state": "Gujarat" }
  ]
}
```

The `orderId` + `stampId` pair here is exactly what `POST /esign` wants. Build a picker from
this rather than asking users for raw IDs.

---

# 7. E-Signing

## `POST /esign` — send a document for signing ⭐

Accepts a **stamp**, a **document**, or **both**. When both, eDrafter **merges the e-stamp onto
the document** and sends the combined PDF.

**Stamp + document — the normal case:**

```bash
# base64 the PDF first
B64=$(base64 -w0 agreement.pdf)

curl -X POST "$BASE/esign" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -d "{
    \"name\": \"Rent Agreement\",
    \"signMethod\": \"aadhaar_otp\",
    \"reason\": \"Tenancy\",
    \"expiryDays\": 7,
    \"orderId\": 100042,
    \"stampId\": 1,
    \"documentName\": \"agreement.pdf\",
    \"documentBase64\": \"$B64\",
    \"signaturePosition\": \"bottom-right\",
    \"signaturePage\": 2,
    \"signatories\": [
      { \"name\": \"Raj Kumar\", \"email\": \"raj@example.com\",   \"phone\": \"9876543210\" },
      { \"name\": \"Priya Sen\", \"email\": \"priya@example.com\", \"phone\": \"9876543211\" }
    ]
  }"
```

| Field | Type | Req | Notes |
|---|---|---|---|
| `name` | string | ✅ | Document name |
| `signMethod` | string | ✅ | `phone_otp` \| `aadhaar_otp` \| `dsc` |
| `signatories` | array | ✅ | `{ name, email, phone? }` — **`phone` required when `signMethod` is `phone_otp`** |
| `orderId` | number | — | Order `_idd` — attaches this e-stamp |
| `stampId` | number | — | Stamp id within that order |
| `documentBase64` | string | — | Base64 PDF/Word, merged **after** the stamp |
| `documentName` | string | — | **Filename with extension** — required for `.doc`/`.docx` → PDF conversion |
| `reason` | string | — | Purpose of signing |
| `expiryDays` | number | — | Link validity, default 7 |
| `signaturePosition` | string | — | `bottom-right` (default) \| `bottom-left` \| `top-right` \| `top-left` |
| `signaturePage` | number | — | Default: last page |
| `signAllPages` | bool | — | Repeats **every** signatory's signature on **every** page |
| `signatories[].allPages` | bool | — | Same, but for **one** signatory only |
| `signatories[].extraPlaceholders` | array | — | Exact spots: `{ page, xNorm, yNorm, wNorm, hNorm }`, values `0`–`1`, **top-left origin** |

See [Signature placement](#signature-placement--all-pages-and-per-signatory-) below for
worked examples of all three.

**Response 200**
```json
{
  "documentId": "66b1c2d3e4f5a6b7c8d9e0f1",
  "uid": "EDR4821",
  "name": "Rent Agreement",
  "status": "sent",
  "signatories": [
    { "name": "Raj Kumar", "email": "raj@example.com", "status": "pending",
      "signUrl": "https://edrafterb2b.in/sign/abc123" },
    { "name": "Priya Sen", "email": "priya@example.com", "status": "pending",
      "signUrl": "https://edrafterb2b.in/sign/def456" }
  ]
}
```

eDrafter emails each signatory **and** returns `signUrl` — so you can deliver the links
yourself (in-app, WhatsApp, your own email) instead of relying on their mail.

> ### ⚠️ `documentBase64` is capped at ~700KB
>
> The request body limit is 1MB and base64 inflates by ~33%, so the practical ceiling is
> **~512KB of actual PDF**. There is no multipart upload endpoint. Validate size at upload
> time and compress before encoding.
>
> The **dashboard** accepts up to 25MB. To sign a larger document through the API, sign an
> existing e-stamp with `orderId` + `stampId` and omit `documentBase64` — that path has no
> size limit. eDrafter will raise the API cap per-account on request.

---

## Signature placement — all pages and per signatory ⭐

Signature placement used to be four corners only, one position for everyone. eDrafter added
per-signatory and coordinate placement in September 2026. There are now **three** levers,
coarse to fine. They compose: `extraPlaceholders` are added *on top of* whatever
`signaturePosition` / `allPages` already produced.

**Verified against the live API on 2026-09-22** with a 2-page PDF — all three forms were
accepted (HTTP 201).

### 1. One signature, chosen corner and page — the default

```json
{
  "signaturePosition": "bottom-right",
  "signaturePage": 2
}
```

`signaturePosition` is one of `bottom-right` (default), `bottom-left`, `top-right`,
`top-left`. `signaturePage` defaults to the **last** page. Both are top-level, so they apply
to every signatory.

### 2. Every signatory on every page — `signAllPages`

```json
{
  "name": "Rent Agreement",
  "signMethod": "phone_otp",
  "documentName": "agreement.pdf",
  "documentBase64": "JVBERi0xLjQK...",
  "signAllPages": true,
  "signaturePosition": "bottom-right",
  "signatories": [
    { "name": "Raj Kumar", "email": "raj@example.com", "phone": "9876543210" },
    { "name": "Priya Sen", "email": "priya@example.com", "phone": "9876543211" }
  ]
}
```

Repeats **both** signatories' signatures on **every** page, in the corner given by
`signaturePosition`. This is the API equivalent of the dashboard's "signature on all pages"
checkbox.

### 3. Per signatory — `allPages` and `extraPlaceholders`

This is the answer to *"how do I change the position for one signatory?"* — move the field
from the top level down into the signatory object.

```json
{
  "name": "Rent Agreement",
  "signMethod": "phone_otp",
  "documentName": "agreement.pdf",
  "documentBase64": "JVBERi0xLjQK...",
  "signaturePosition": "bottom-right",
  "signaturePage": 1,
  "signatories": [
    {
      "name": "Raj Kumar",
      "email": "raj@example.com",
      "phone": "9876543210",
      "allPages": true
    },
    {
      "name": "Priya Sen",
      "email": "priya@example.com",
      "phone": "9876543211",
      "allPages": false,
      "extraPlaceholders": [
        { "page": 2, "xNorm": 0.55, "yNorm": 0.35, "wNorm": 0.30, "hNorm": 0.08 }
      ]
    }
  ]
}
```

Raj signs every page in the bottom-right corner. Priya signs page 1 in the bottom-right
corner (from the top-level defaults) **plus** one extra box at an exact spot on page 2.

**`extraPlaceholders` coordinates** are normalised fractions of the page, `0`–`1`, with a
**top-left origin** — so `yNorm: 0` is the top of the page, not the bottom:

| Field | Meaning | Example |
|---|---|---|
| `page` | 1-based page number | `2` |
| `xNorm` | Left edge, as a fraction of page **width** | `0.55` → 55% across |
| `yNorm` | Top edge, as a fraction of page **height**, **from the top** | `0.35` → 35% down |
| `wNorm` | Box width, as a fraction of page width | `0.30` → 30% wide |
| `hNorm` | Box height, as a fraction of page height | `0.08` → 8% tall |

Because the values are normalised, they are paper-size independent — the same numbers land
in the same relative spot on A4 and Letter.

> **⚠️ These fields are not validated at submission.** A placeholder with `"page": 99` on a
> 2-page PDF was accepted with **HTTP 201** and no error. The response echoes back neither
> `signAllPages` nor `extraPlaceholders`, so **a 201 is not evidence your placement was
> honoured**. Verify visually by opening the returned `signUrl` before trusting a new
> placement config in production.

## `GET /esign` — list documents

```bash
curl -H "x-api-key: $KEY" "$BASE/esign"
```

**Response 200**
```json
{
  "count": 12,
  "documents": [
    { "documentId": "66b1c2d3e4f5a6b7c8d9e0f1", "name": "Rent Agreement",
      "status": "completed", "signatoryCount": 2, "signedCount": 2,
      "signedUrl": "https://.../rent-agreement-signed.pdf" }
  ]
}
```

## `GET /esign/:id` — who has signed

```bash
curl -H "x-api-key: $KEY" "$BASE/esign/66b1c2d3e4f5a6b7c8d9e0f1"
```

**Response 200**
```json
{
  "documentId": "66b1c2d3e4f5a6b7c8d9e0f1",
  "status": "in_progress",
  "signatories": [
    { "name": "Raj Kumar", "status": "signed",  "signedAt": "2026-06-01T10:22:00.000Z" },
    { "name": "Priya Sen", "status": "pending", "signedAt": null }
  ]
}
```

**Poll this.** There is no per-signatory webhook — partial progress, declines and expiry are
only visible here.

## `GET /esign/:id/signed` — download the signed PDF

```bash
curl -H "x-api-key: $KEY" "$BASE/esign/66b1c2d3e4f5a6b7c8d9e0f1/signed"
```

**Response 200**
```json
{
  "documentId": "66b1c2d3e4f5a6b7c8d9e0f1",
  "status": "completed",
  "signedUrl": "https://edrafterb2b.in/uploads/rent-agreement-signed.pdf"
}
```

Available **only once `status` is `completed`** — otherwise `409`. **Fetch and archive the PDF
to your own storage immediately**; the URL's lifetime is undocumented.

---

# 8. Wallet

## `POST /topups` — request a top-up

```bash
curl -X POST "$BASE/topups" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "amount": 50000,
    "txnNumber": "UTR123456789",
    "paymentNote": "NEFT from HDFC"
  }'
```

`txnNumber` is your bank/UPI reference for the transfer you already made.

**Response 201**
```json
{ "message": "Top-up request created", "amount": 50000,
  "txnNumber": "UTR123456789", "status": "Pending" }
```

> ⚠️ **Credited only after an eDrafter admin approves it.** One pending request at a time, and
> it is **not available on Pay-As-Per-Order accounts**. There is **no instant/card payment
> endpoint** — plan lead time, or the wallet runs dry mid-flow and orders start failing `402`.

## `GET /topups` — list top-up requests

```bash
curl -H "x-api-key: $KEY" "$BASE/topups"
```

```json
[ { "amount": 50000, "txnNumber": "UTR123456789", "status": "Approved" } ]
```

## `GET /transactions` — wallet ledger

```bash
curl -H "x-api-key: $KEY" "$BASE/transactions?type=Debit&from=2026-01-01&limit=200"
```

Filters: `type` (`Debit` | `Credit`), `from`, `to`, `limit` (default 200, max 1000).

**Response 200**
```json
{
  "count": 128,
  "currentBalance": 48410,
  "summary": { "totalCredit": 200000, "totalDebit": 151590 },
  "transactions": [
    { "type": "Debit", "amount": 1590,
      "balanceBefore": 50000, "balanceAfter": 48410,
      "description": "Purchased Stamps Order ID 100042",
      "createdAt": "2026-06-04T09:15:00.000Z" }
  ]
}
```

**Use it for:** reconciliation — match every debit against an order row in your own database.

---

# 9. Webhooks

Register a URL and eDrafter POSTs a signed JSON body the moment an event fires.

| | |
|---|---|
| **Supported events** | `order.completed`, `esign.completed` — **that's all** |
| **Retries** | Up to 5, exponential backoff; on **timeout, 5xx, or 429** |
| **Timeout** | 10s — respond `2xx` fast, process asynchronously |
| **Signature** | `X-eDrafter-Signature: sha256=<hmac>` — HMAC-SHA256 of the **raw body** |

## `POST /webhooks` — register

```bash
curl -X POST "$BASE/webhooks" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "url": "https://your-app.com/webhooks/edrafter",
    "events": ["order.completed", "esign.completed"],
    "description": "Production listener"
  }'
```

**Response 201**
```json
{
  "message": "Webhook registered",
  "webhook": {
    "_id": "66d1e2f3a4b5c6d7e8f9a0b1",
    "url": "https://your-app.com/webhooks/edrafter",
    "events": ["order.completed", "esign.completed"],
    "secret": "whsec_9f8e7d6c5b4a3210fedcba9876543210",
    "active": true
  }
}
```

⚠️ **Save the `secret` — it is shown once.** Put it in Key Vault alongside the API key.

## `GET /webhooks` — list

```bash
curl -H "x-api-key: $KEY" "$BASE/webhooks"
```

Returns `supportedEvents`, each hook's config, `active`, and last delivery status.

## `DELETE /webhooks/:id` — delete

```bash
curl -X DELETE -H "x-api-key: $KEY" "$BASE/webhooks/66d1e2f3a4b5c6d7e8f9a0b1"
```

## What a delivery looks like

```http
POST /webhooks/edrafter HTTP/1.1
Host: your-app.com
Content-Type: application/json
X-eDrafter-Event: order.completed
X-eDrafter-Signature: sha256=4f2a...c91b

{
  "event": "order.completed",
  "data": {
    "orderId": 100042,
    "status": "Completed",
    "quantity": 10,
    "denomination": 100,
    "total": 1590,
    "stampCount": 10,
    "completedAt": "2026-06-04T09:30:00.000Z"
  },
  "attempt": 1,
  "deliveredAt": "2026-06-04T09:30:01.000Z"
}
```

`esign.completed` carries `documentId`, `name`, `status`, and `signedUrl`.

## Verifying the signature in .NET

```csharp
// Program.cs — you MUST read the raw body. JSON model binding normalises
// whitespace and will break the HMAC.
app.Use(async (ctx, next) => { ctx.Request.EnableBuffering(); await next(); });

[HttpPost("/webhooks/edrafter")]
public async Task<IActionResult> Receive()
{
    Request.Body.Position = 0;
    using var reader = new StreamReader(Request.Body, leaveOpen: true);
    var rawBody = await reader.ReadToEndAsync();

    var received = Request.Headers["X-eDrafter-Signature"].ToString();
    var expected = "sha256=" + Convert.ToHexString(
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_secret),
            Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

    // fixed-time comparison, never ==
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(received),
            Encoding.UTF8.GetBytes(expected)))
        return Unauthorized();

    // Queue and return fast — the timeout is 10 seconds.
    await _queue.EnqueueAsync(rawBody);
    return Ok();
}
```

> ⚠️ **Three things to get right:** read the **raw body** (`EnableBuffering()`); use a
> **fixed-time** comparison; make the handler **idempotent** — 5 retries mean duplicate
> deliveries are expected, not exceptional. Key your idempotency on `orderId` / `documentId`.

**Missing events:** there is no webhook for order failure, e-sign expiry, e-sign decline, or
individual signatures. Those need polling — `GET /orders/:idd` and `GET /esign/:id`.

---

# 10. End-to-end walkthrough

One document, digital-only, from nothing to a signed PDF.

```bash
export KEY="your_api_key_here"
export BASE="https://edrafterb2b.in/api/v1"

# ── Setup (once) ──────────────────────────────────────────────────────────
# 0. Confirm the key works and note the balance
curl -H "x-api-key: $KEY" "$BASE/me"

# 0b. Register a webhook so you don't have to poll (save the secret!)
curl -X POST "$BASE/webhooks" -H "x-api-key: $KEY" -H "Content-Type: application/json" \
  -d '{"url":"https://your-app.com/webhooks/edrafter",
       "events":["order.completed","esign.completed"]}'

# ── Build the form ────────────────────────────────────────────────────────
# 1. Which states can we serve?
curl -H "x-api-key: $KEY" "$BASE/states"

# 2. product_id for the chosen state
curl -H "x-api-key: $KEY" "$BASE/products"

# 3. Rules for that state — denominations, required fields, char limits
curl -H "x-api-key: $KEY" "$BASE/states/Karnataka/rules"

# 4. Article codes (document types) for that state
curl -H "x-api-key: $KEY" "$BASE/states/Karnataka/articles"

# ── Check before you spend (both free) ────────────────────────────────────
# 5. Are the user's inputs legal?
curl -X POST "$BASE/validate/order" -H "x-api-key: $KEY" -H "Content-Type: application/json" \
  -d '{"state":"Karnataka","firstParty":"Ramesh Kumar","secondParty":"Jane Smith","denomination":500}'

# 6. Exact price — confirm "affordable": true
curl -X POST "$BASE/orders/quote" -H "x-api-key: $KEY" -H "Content-Type: application/json" \
  -d '{"product_id":"664a...","quantity":1,"denomination":500,"doorstepDelivery":false}'

# ── Spend ─────────────────────────────────────────────────────────────────
# 7. Place the order — wallet debited. Status: Pending. NEVER auto-retry.
curl -X POST "$BASE/orders" -H "x-api-key: $KEY" -H "Content-Type: application/json" \
  -d '{"firstParty":"Ramesh Kumar","secondParty":"Jane Smith","purchasedBy":"Ramesh Kumar",
       "dutyPaidBy":"Ramesh Kumar","product_id":"664a...","purpose":"Sale Agreement",
       "quantity":1,"denomination":500,"doorstepDelivery":false,"refId":"REF-2026-88231"}'
#    → { "order": { "_idd": 100042, "status": "Pending" } }

#    ⏳ 1 hour – 2 working days. Wait for the order.completed webhook,
#       or poll GET /orders/100042 until status = Completed.

# ── Collect the stamp ─────────────────────────────────────────────────────
# 8. Stamp links (⚠ downloading marks the stamp Used)
curl -H "x-api-key: $KEY" "$BASE/orders/100042/stamps"

# ── Sign ──────────────────────────────────────────────────────────────────
# 9. Send stamp + document for signing — eDrafter merges them
B64=$(base64 -w0 agreement.pdf)
curl -X POST "$BASE/esign" -H "x-api-key: $KEY" -H "Content-Type: application/json" \
  -d "{\"name\":\"Sale Agreement\",\"signMethod\":\"aadhaar_otp\",
       \"orderId\":100042,\"stampId\":1,
       \"documentName\":\"agreement.pdf\",\"documentBase64\":\"$B64\",
       \"signatories\":[{\"name\":\"Ramesh Kumar\",\"email\":\"ramesh@example.com\"}]}"
#    → { "documentId": "66b1...", "status": "sent", "signatories": [{ "signUrl": "..." }] }

#    ⏳ Signatories sign. Wait for esign.completed, or poll GET /esign/66b1...

# 10. Download the signed PDF and archive it your side
curl -H "x-api-key: $KEY" "$BASE/esign/66b1.../signed"
```

**The two waits are the whole architectural story:** step 7→8 (hours to days) and step 9→10
(however long humans take). Neither can be a blocking call or a spinner. Persist your own state
machine, drive it from webhooks, and poll as a fallback.

---

# 11. Error responses

Errors come back as JSON:

```json
{ "error": "Insufficient wallet balance", "code": 402 }
```

| Code | Meaning | How to handle |
|---|---|---|
| **200 / 201** | Success | |
| **400** | Missing or invalid field | Pre-empt with `POST /validate/order` |
| **401** | **No** `x-api-key` header | Config error — alert ops, don't show the user |
| **402** | **Insufficient wallet balance** | Block checkout, alert finance. Check `affordable` on the quote first |
| **403** | Invalid or disabled key | Config error — alert ops |
| **404** | Not found — order, product, document, refId | 404 in your UI |
| **409** | **Conflict** — stamp already used, or document not fully signed | Refresh state and re-render |
| **410** | **Gone** — self-expiring link expired | Generate a new link |
| **429** | Rate limited | Back off and retry |
| **500** | Server error | Retry with backoff — **except `POST /orders`**, which must never be auto-retried |

---

# Quick reference: all 27 endpoints

| # | Method | Path | Cost |
|---|---|---|---|
| 1 | GET | `/me` | free |
| 2 | GET | `/products` | free |
| 3 | GET | `/products/:id` | free |
| 4 | GET | `/states` | free |
| 5 | GET | `/states/:state/articles` | free |
| 6 | GET | `/states/:state/rules` | free |
| 7 | POST | `/validate/order` | free |
| 8 | POST | `/orders/quote` | free |
| 9 | POST | `/orders` | 💸 **debits wallet** |
| 10 | GET | `/orders` | free |
| 11 | GET | `/orders/:idd` | free |
| 12 | GET | `/orders/by-ref/:refId` | free |
| 13 | PUT | `/orders/:idd/cancel` | refunds wallet |
| 14 | GET | `/orders/:idd/stamps` | free |
| 15 | GET | `/orders/:idd/stamps/:stampId/download` | ⚠️ marks stamp Used |
| 16 | POST | `/orders/:idd/stamps/:stampId/link` | free |
| 17 | GET | `/dl/:token` | ⚠️ marks stamp Used |
| 18 | GET | `/stamps` | free |
| 19 | POST | `/esign` | cost unconfirmed |
| 20 | GET | `/esign` | free |
| 21 | GET | `/esign/:id` | free |
| 22 | GET | `/esign/:id/signed` | free |
| 23 | POST | `/topups` | free (needs admin approval) |
| 24 | GET | `/topups` | free |
| 25 | GET | `/transactions` | free |
| 26 | POST | `/webhooks` | free |
| 27 | GET | `/webhooks` | free |
| — | DELETE | `/webhooks/:id` | free |

---

# The 13 things that will bite you

| # | Gotcha |
|---|---|
| 1 | **`_idd`, not `_id`** — orders use a display ID everywhere downstream |
| 2 | **Downloading a stamp marks it "Used"** — not e-signing it. Download once, deliberately |
| 3 | **`downloadUrl` embeds the API key.** Never log, render, or email it — use `/link` |
| 4 | **Signature placement is now per-signatory** — `signAllPages`, `signatories[].allPages`, `signatories[].extraPlaceholders`. But none of it is validated: `"page": 99` on a 2-page PDF returns **201**. Verify visually |
| 5 | **~512KB PDF limit** (700KB base64). No multipart upload. Compress and validate at upload |
| 6 | **`doorstepDelivery: false` ⇒ shipping ₹0** — the single biggest cost lever |
| 7 | **Production only.** Every `POST /orders` spends real money. `quote` and `validate` are free |
| 8 | **Stamps are single-use.** Reuse returns `409` |
| 9 | **Top-ups need manual admin approval.** No instant payment — watch the balance |
| 10 | **Never auto-retry `POST /orders`** — not idempotent, may double-charge. Recover via `refId` |
| 11 | **Webhooks retry 5×** — the handler must be idempotent |
| 12 | **`dutyPaidBy` must exactly equal `firstParty` or `secondParty`** — dropdown, not free text |
| 13 | **`signMethod` has no `email_otp`.** Only `phone_otp`, `aadhaar_otp`, `dsc` — anything else is a **400**. Despite eDrafter calling phone OTP "upcoming", `phone_otp` is accepted and returns 201 |

---

**Related docs:** [06 API Reference](06-API-REFERENCE.md) (the what and why) ·
[04 Implementation Flow](04-IMPLEMENTATION-FLOW.md) (the build plan) ·
[01 Understanding the Flow](01-UNDERSTANDING-THE-FLOW.md) ·
[02 Pricing](02-PRICING-AND-COST-PER-DOCUMENT.md)
