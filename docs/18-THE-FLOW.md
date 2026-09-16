# How the eDrafter POC works

**Scope:** Karnataka, article `30(1)(i)` — residential lease not exceeding 12 months.
**Status:** Proven end to end against the live eDrafter API on 2026-09-16.

For setup and configuration see [19-SETUP-AND-CONFIG.md](19-SETUP-AND-CONFIG.md).

---

## The three pieces

| Piece | Port | What it is |
|---|---|---|
| `edrafter-ui` | 4200 | Angular. The form, the cost gates, the status timeline. |
| `EDrafter.Api` | 5100 | .NET. Everything that talks to eDrafter, and the spend ledger. |
| `EDrafter.MockServer` | 5099 | A **fake eDrafter** for testing without spending. Optional. |

The UI never calls eDrafter directly — the API key stays on the server.

---

## The journey

```
  ONE FORM
     │  11 fields, all required
     ▼
  quote (free)  ──────────────  nothing is charged here
     │
     │  ◆ GATE 1 — checkbox + button, shows the exact amount
     ▼
  POST /orders              💸 ~₹109   refundable while Pending
     │
     ⋮  a human at eDrafter accepts the order, then ~1 working hour
     ⋮  Mon–Fri 9–6, cut-off 3:30pm. Friday afternoon → Monday.
     ▼
  order.completed webhook   ──►  stamp downloaded and archived
     │
     │  ◆ GATE 2 — checkbox + button, states it is NOT refundable
     ▼
  POST /esign               💸 ₹12     charged at LINK GENERATION
     │                                 per signatory, never refunded
     ⋮  the signatory opens the link, enters a phone OTP
     ▼
  esign.completed webhook   ──►  signed PDF downloaded and archived
     │
     ▼
  DONE — status "Signed"
```

### What it costs

Verified against production, consideration ₹10,000, one signatory by phone OTP:

| | |
|---|---|
| Stamp duty (0.5%, capped ₹500) | ₹50.00 |
| eDrafter service charge | ₹50.00 |
| GST 18% on the service charge | ₹9.00 |
| **Order total** | **₹109.00** |
| e-Sign, 1 signatory | ₹12.00 |
| **Grand total** | **₹121.00** |

GST applies to the service charge only — never to stamp duty, never to e-sign.

⚠️ Our older pricing docs use a **₹55** service charge from the proposal PDF. Production
charges **₹50**. Every ₹55-based figure is ₹5.90 too high.

---

## Our states vs eDrafter's

Two separate state machines. We own ours; eDrafter is a downstream dependency.

**Ours** — what the UI shows:

| Status | Meaning |
|---|---|
| `Draft` | Form saved. Nothing spent. |
| `OrderPlaced` | 💸 Wallet debited, waiting on the stamp |
| `OrderProcessing` | eDrafter accepted it |
| `OrderOnHold` | eDrafter paused it. Money already spent — a wait, not a failure. |
| `OrderRejected` | eDrafter rejected it outright |
| `StampReady` | Stamp issued and archived. **Ready to send for signing.** |
| `SentForSigning` | 💸 e-Sign charged, link sent, **non-refundable** |
| `PartiallySigned` | Some signed, not all |
| `Signed` | Done — signed PDF archived |
| `Failed` | Declined or expired. **The charge stands.** |
| `NeedsReview` | ⚠️ A spending call timed out. Reconcile — never retry. |
| `Cancelled` | Cancelled while `Pending`, refunded |

**Theirs** — seven statuses, though their documentation lists only four:

`Payment Pending` · `Requested` · **`Pending`** · **`Processing`** · `Hold` ·
**`Completed`** · `Shipped` · **`Cancelled`**

The four in bold are documented. The rest we found in their dashboard and live API.

> We never branch on their status string alone. `GET /orders/:idd/stamps` returns an
> explicit **`ready: true`** flag — that is the signal to trust, and it is what their own
> dashboard means by *"the e-stamp is ready to attach"*.

---

## Spend safety

Three layers. The first is structural, not a matter of discipline.

### 1. The idempotency ledger

Every spending call writes a `spend_attempt` row **before** the HTTP request, with a
**UNIQUE** key derived from the operation (`order:{agreementId}`), never random. A
duplicate insert fails before eDrafter is ever called.

**Verified: 8 simultaneous order requests produced exactly 1 charge.**

### 2. No retry policy

`POST /orders` and `POST /esign` have **zero** retries registered. Polly's
retry-on-everything is precisely the bug that double-charges, because neither call is
idempotent. Free GETs retry freely.

### 3. The arming switch

`EDrafter:ArmSpending` — pointing at the live API is **not enough** to spend. It is
`false` in every checked-in config, so it cannot arrive already-armed from version
control.

*(A fourth layer, `MaxTotalSpendPaise`, is currently disabled — set to 0.)*

### The UNKNOWN outcome — what all this exists for

A timeout or 5xx means the money **may or may not** have moved. It is recorded as
`Unknown`, never as a failure, and **never retried automatically**:

```
POST /api/reconcile
   order  →  GET /orders/by-ref/{our refId}     (free, read-only)
   esign  →  GET /esign, newest first, match on document name
        found     →  adopt it; the spend already happened
        not found →  flag for a human. Still NO auto-retry.
```

The UI shows this as its own state with a Reconcile button and **no retry button**.

---

## Webhooks and polling

eDrafter publishes three events: `order.completed`, `order.cancelled`, `esign.completed`.

**Webhooks are primary, polling is the fallback** — and polling is the *only* way to see
several things at all. There is **no event** for e-sign decline, e-sign expiry, individual
signatures, or order rejection.

Each delivery is verified by HMAC-SHA256 over the **raw body** with a fixed-time compare,
then deduplicated: eDrafter retries up to 5 times, so the same event *will* arrive twice.
A second delivery is a no-op — not a second PDF, and emphatically not a second
`POST /esign`.

| Event | Tested against production? |
|---|---|
| `order.completed` | ✅ verified |
| `esign.completed` | ✅ verified |
| `order.cancelled` | ❌ **never fired** when a real order was cancelled |

---

## Decisions worth knowing

**Only the second party signs.** Configurable (`Esign:Signatories`), set to `second`.
e-Sign is charged per signatory, so this halves the cost. The generated PDF reflects it:
the Lessee's block says *"Signed electronically via eDrafter"*, the Lessor's is a plain
signature line. Whether one-sided e-signing is legally sufficient is a question for legal
advice, not the API.

**Aadhaar is not collected.** `POST /esign` accepts only name, email and phone per
signatory. Aadhaar was never transmitted, so the field was dropped entirely — no column,
no masking, no log redaction.

**We generate the PDF, we do not upload one.** Output is ~64KB against a ~512KB limit —
about 13% of the budget. An uploaded scan would routinely exceed it.

**We do not merge the stamp.** `POST /esign` takes `orderId` + `stampId` alongside
`documentBase64` and eDrafter merges it. Doing it ourselves too would produce two stamps.

**The signature lands on one page.** `signaturePage` and `signaturePosition` are
top-level fields, not per-signatory. We draw visible signature blocks on every page and
the footer states plainly where the binding signature actually is.

---

## Where things are stored

```
EDrafter.Api/
  edrafter-poc.db          SQLite: agreements, signatories, spend ledger, webhook dedupe
  storage/
    agreements/            the PDF we generate
    stamps/                the e-stamp, as issued
    signed/                the completed signed document
```

Delete both to start clean — see [19-SETUP-AND-CONFIG.md](19-SETUP-AND-CONFIG.md).

---

## Known eDrafter quirks

All found by calling the live API, all differing from their documentation.

| # | Quirk | What we do |
|---|---|---|
| 1 | `validate/order` **ignores `articleCode`** — it reads `article` | Send both |
| 2 | Same on `/rules`: `?articleCode=` stays `pending`, `?article=` resolves | Send both |
| 3 | `GET /esign/:id/signed` returns **JSON with a `signedUrl`**, not the PDF | Follow the URL |
| 4 | Service charge is **₹50**, not the ₹55 in the proposal | Use the live quote |
| 5 | **No duty rule** for `30(1)(i)` — returns `unconstrained` | Compute it from config |
| 6 | Four order statuses are undocumented | Treat status as an open string |
| 7 | `certificateNo` comes back **empty** | Test account; key the UI off status |
| 8 | `GET /orders` returns a **bare array**, not `{count, orders}` | Handle both |
| 9 | `order.cancelled` **did not fire** on a real cancellation | Polling covers it |

Full detail in [17-LIVE-API-FINDINGS.md](17-LIVE-API-FINDINGS.md).
