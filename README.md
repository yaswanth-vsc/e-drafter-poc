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

## Documentation

| | |
|---|---|
| [docs/18-THE-FLOW.md](docs/18-THE-FLOW.md) | What the system does, the states, the costs |
| [docs/19-SETUP-AND-CONFIG.md](docs/19-SETUP-AND-CONFIG.md) | Setup, every setting, webhook registration |
| [docs/13-API-EXAMPLES.md](docs/13-API-EXAMPLES.md) | Every eDrafter endpoint with a working request |

Nine eDrafter behaviours differ from their documentation and are worked around here —
see the findings section of `18-THE-FLOW.md`.
