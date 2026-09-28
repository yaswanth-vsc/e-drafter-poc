# e-drafter-poc

Lease agreements end to end against the **eDrafter B2B API** — order an e-stamp, generate
the agreement, send it for e-signature, archive the signed document.

**Scope:** Karnataka, article `30(1)(i)` — residential lease not exceeding 12 months.
**Status:** Proven against the live eDrafter API, 2026-09-16.

---

## The three pieces

| | Stack | Port |
|---|---|---|
| `src/edrafter-ui` | Angular 19 | 4200 |
| `src/EDrafter.Api` | .NET 10, EF Core, SignalR | 5100 |
| `src/EDrafter.MockServer` | a **fake eDrafter**, for testing without spending | 5099 |

## The flow

```
FORM → quote (free) → ◆ confirm → POST /orders   💸 ~₹109
                                       ⋮ webhook
                                   stamp ready
                                       ↓
                      generate PDF → ◆ confirm → POST /esign   💸 ₹12
                                       ⋮ webhook            non-refundable
                                   signed PDF archived
```

## Running it

```bash
cd src
dotnet run --project EDrafter.Api --launch-profile api     # terminal 1
cd edrafter-ui && npx ng serve                             # terminal 2
```

Then open **http://localhost:4200**.

Out of the box this points at the **mock** with spending disarmed — a fresh clone cannot
spend money. Real credentials go in `src/EDrafter.Api/appsettings.Local.json`, which is
gitignored.

## Why the spend guards exist

`POST /orders` and `POST /esign` are irreversible and **not idempotent**, and e-sign is
charged at link generation with no refund for a decline, cancellation or expiry.

- an **idempotency ledger** with a UNIQUE derived key, written *before* the call —
  verified: 8 concurrent requests produced exactly 1 charge
- **no retry policy** on either spending call
- an **arming switch**: pointing at the live API is not enough to spend
- a timeout is recorded as **UNKNOWN** and reconciled, never retried

## Signing: placement and `signMethod`

`POST /esign` sends a document for signature. Two things about it are worth knowing before
you write the payload, because both contradict what the vendor documentation says.

### `signMethod` — there is no `email_otp`

Only three values are accepted. Anything else is a **400**:

```
{"message":"signMethod must be one of: phone_otp, dsc, aadhaar_otp"}
```

eDrafter's support reply calls phone OTP "upcoming / currently unavailable", but
`phone_otp` **is** accepted and returns `201`. Confirm delivery actually reaches the
signatory before relying on it.

### Signature placement — three levers

Placement used to be four corners shared by everyone. Since September 2026 it is
per-signatory. The three levers compose — `extraPlaceholders` are added *on top of* whatever
`signaturePosition` and `allPages` already produced.

| Lever | Scope | Use it for |
|---|---|---|
| `signaturePosition` + `signaturePage` | top-level, everyone | one signature in a chosen corner |
| `signAllPages: true` | top-level, everyone | every signatory on every page |
| `signatories[].allPages` | **one signatory** | all pages, for that person only |
| `signatories[].extraPlaceholders` | **one signatory** | exact x/y boxes |

**To change the position for one signatory, move the field out of the top level and into
that signatory's object.** Here Raj signs every page bottom-right; Priya signs page 1
bottom-right *plus* one box at an exact spot on page 2:

```json
{
  "name": "Rent Agreement",
  "signMethod": "phone_otp",
  "reason": "Tenancy",
  "expiryDays": 7,
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

`extraPlaceholders` coordinates are fractions of the page, `0`–`1`, with a **top-left
origin** — `yNorm: 0` is the top. Being normalised, they are paper-size independent.

**Response `201`** — `documentId` is what you poll with, and `signUrl` is the signing link
(a bearer token in a URL: never log or render it):

```json
{
  "documentId": "6ab247b5707a69282af06e0d",
  "uid": "EDR32FD673A16",
  "status": "sent",
  "signMethod": "phone_otp",
  "signatories": [
    { "name": "Raj Kumar", "email": "raj@example.com", "status": "pending",
      "signUrl": "https://edrafterb2b.in/sign/<id>.<token>" }
  ]
}
```

> **⚠️ Placement is not validated, and a `201` does not prove it was applied.** A placeholder
> with `"page": 99` on a 2-page PDF was accepted with `201` and no error, and the response
> echoes back neither `signAllPages` nor `extraPlaceholders`. Verify a new placement config
> visually via `signUrl` before shipping it.

Full field table and the `documentBase64` size limit:
[docs/13-API-EXAMPLES.md](docs/13-API-EXAMPLES.md#signature-placement--all-pages-and-per-signatory-).

## Documentation

| | |
|---|---|
| [docs/18-THE-FLOW.md](docs/18-THE-FLOW.md) | What the system does, the states, the costs |
| [docs/19-SETUP-AND-CONFIG.md](docs/19-SETUP-AND-CONFIG.md) | Setup, every setting, webhook registration |
| [docs/13-API-EXAMPLES.md](docs/13-API-EXAMPLES.md) | Every eDrafter endpoint with a working request |
| [docs/21-WEBHOOKS.md](docs/21-WEBHOOKS.md) | Registering and locally testing the eDrafter and Zoho webhooks |
| [docs/22-HOW-TO-RUN.md](docs/22-HOW-TO-RUN.md) | Running it, what costs money, endpoints, troubleshooting |

Nine eDrafter behaviours differ from their documentation and are worked around here —
see the findings section of `18-THE-FLOW.md`.
