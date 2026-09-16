# eDrafter POC — Source

**Scope:** Karnataka only, article `30(1)(i)` (residential lease ≤ 12 months), quantity 1.
See [../docs/15-POC-SCOPE-DECISIONS.md](../docs/15-POC-SCOPE-DECISIONS.md).

**Status:** Steps 1–6 complete and verified. Step 7 (one live run) is not done — it spends
real money and needs a funded wallet plus explicit sign-off.

---

## Projects

| Project | What it is | Port | Spends money? |
|---|---|---|---|
| `EDrafter.MockServer` | Stand-in for **eDrafter's** API — a fake eDrafter, not our code | 5099 | **Never** |
| `EDrafter.Api` | **Our backend.** .NET 10, EF Core, SignalR | 5100 | Only when armed |
| `edrafter-ui` | **Our frontend.** Angular 19 | 4200 | — |

---

## Running it

**Three terminals, started in this order.** Each must stay open — closing one stops that
service. Order matters: the backend calls the mock at startup, and the UI calls the backend.

**Terminal 1 — the fake eDrafter**
```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
dotnet run --project EDrafter.MockServer --launch-profile mock
```
Wait for `Now listening on: http://localhost:5099`.

**Terminal 2 — the backend**
```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
dotnet run --project EDrafter.Api --launch-profile api
```
Wait for `Now listening on: http://localhost:5100`.

**Terminal 3 — the UI**
```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src\edrafter-ui
npx ng serve
```

Then open **http://localhost:4200**.

Webhooks need no setup: the backend registers its callback with the mock at startup and
captures the signing secret itself. To use a secret you control instead, set
`EDrafter__WebhookSecret` before starting the API.

### "Cannot reach the API. Is it running on :5100?"

The UI is up but the backend is not. Start terminal 2. If it refuses to build with
*"the file is locked by EDrafter.Api"*, an older copy is still running — stop it first:

```bash
netstat -ano | grep LISTENING | grep ':5100 '   # find the PID
taskkill //PID <pid> //F
```

### Stopping everything

Ctrl-C in each terminal, or:

```bash
for p in 5099 5100 4200; do
  pid=$(netstat -ano | grep LISTENING | grep ":$p " | head -1 | awk '{print $NF}')
  [ -n "$pid" ] && taskkill //PID $pid //F
done
```

### Starting from a clean slate

The SQLite database and the generated PDFs persist between runs. To wipe them:

```bash
rm -f EDrafter.Api/edrafter-poc.db && rm -rf EDrafter.Api/storage
```

---

## The flow

```
ONE FORM  →  quote (free)  →  confirm  →  POST /orders        💸 spend 1
                                              ⋮ webhook
                                          stamp ready
                                              ↓
                          generate PDF  →  confirm  →  POST /esign   💸 spend 2
                                              ⋮ webhook            (non-refundable)
                                          signed PDF archived
```

---

## Spend safety — how a double charge is made impossible

Four independent layers. The first is structural, not a matter of discipline:

| Layer | Mechanism |
|---|---|
| **Idempotency ledger** | A `spend_attempt` row with a **UNIQUE** `idempotency_key`, written **before** the call. The key is derived (`order:{agreementId}`), never random — so one agreement cannot produce two orders. **Verified: 8 concurrent requests → exactly 1 charge.** |
| **No retry policy** | `POST /orders` and `POST /esign` have **zero** retries registered. Polly's retry-on-everything is exactly the bug that double-charges. |
| **Arming switch** | `EDrafter:ArmSpending` is `false` in every checked-in config. Pointing at the live API is **not enough** to spend. |
| **Budget ceiling** | `EDrafter:MaxTotalSpendPaise` (default ₹200). A loop bug cannot drain the wallet. |

### The UNKNOWN outcome — the case this is all for

A timeout or 5xx means the money **may or may not** have moved. It is recorded as
`Unknown`, never as a failure, and **never retried automatically**. The UI shows it as its
own state with a Reconcile button and no retry button:

```
POST /api/reconcile
   order  -> GET /orders/by-ref/{our refId}    (free, read-only)
   esign  -> GET /esign (newest first), match on document name
        found     -> adopt it; the spend already happened
        not found -> flag for a human. Still NO auto-retry.
```

---

## Endpoints

| Method | Path | Spends? |
|---|---|---|
| `GET` | `/api/health` | no |
| `GET` | `/api/rules` | no — form validation, read from eDrafter |
| `POST` | `/api/agreements` | no — create a draft |
| `GET` | `/api/agreements/{id}/quote` | no — **full cost preview, both charges** |
| `POST` | `/api/agreements/{id}/generate-pdf` | no |
| `GET` | `/api/agreements/{id}/pdf` | no — preview before sending |
| `POST` | `/api/agreements/{id}/order` | 💸 **yes** |
| `POST` | `/api/agreements/{id}/prepare-and-send` | 💸 **yes, non-refundable** |
| `GET` | `/api/agreements/{id}/signed-pdf` | no |
| `POST` | `/api/reconcile` | no — the recovery path |
| `POST` | `/webhooks/edrafter` | no — HMAC-verified receiver |
| — | `/hubs/agreements` | SignalR, for live status |

---

## Webhooks against the real eDrafter

**Locally you need none of this** — the backend registers its callback with the mock at
startup and captures the signing secret itself.

This section is for pointing the **real** eDrafter at your machine.

### Why a tunnel is needed

eDrafter delivers webhooks by making an HTTP request **to you**. They cannot reach
`http://localhost:5100` — on their server, "localhost" means their own machine. A tunnel
gives yours a temporary public https address:

```
eDrafter  ->  https://xyz.loca.lt  ->  your machine  ->  localhost:5100
```

### One command

```bash
cd src
npm install                                    # once — installs localtunnel
npm run tunnel:register -- --key YOUR_API_KEY
```

That opens the tunnel, registers the public url with eDrafter, and prints the signing
secret. **Leave it running** — closing it drops the tunnel and deliveries start failing.

Then, in another terminal, restart the backend with the secret it printed:

```bash
export EDrafter__WebhookSecret="whsec_..."     # PowerShell: $env:EDrafter__WebhookSecret="..."
dotnet run --project EDrafter.Api --launch-profile api
```

Also set `Webhooks:AutoRegister` to `false`, so the backend does not register a second
localhost hook over the top of the tunnelled one.

### Other commands

```bash
npm run tunnel                                 # tunnel only, register nothing
npm run webhook:list                           # what is registered (mock by default)
node register-webhook.js --base https://edrafterb2b.in/api/v1 --key KEY --list
node register-webhook.js --base ... --key KEY --delete <webhook-id>
npm run tunnel:register -- --key KEY --provider ngrok   # needs @ngrok/ngrok + NGROK_AUTHTOKEN
```

### Three things that will bite you

| | |
|---|---|
| **The secret is shown once** | eDrafter returns it at creation and never again. Lose it and you must delete the webhook and register a new one. |
| **The url changes every restart** | localtunnel issues a new subdomain each time, so the old webhook is dead. Delete it and re-register, or use ngrok with a reserved domain. |
| **Only two events exist** | `order.completed` and `esign.completed`. There is **no** webhook for order failure, e-sign decline, e-sign expiry, or individual signatures — polling covers those, which is why `PollingService` runs alongside. |

---

## Seeing what we send to eDrafter

Every eDrafter call logs its full request and response — url, headers, payload, status
code, and duration — paired by a short correlation id:

```
eDrafter REQUEST  [27c62798] POST http://.../api/v1/orders
  headers: x-api-key=098a…ea (64 chars)  Content-Type=application/json
  body: {"firstParty":"Log Test",...,"denomination":50.0,"refId":"EDR-..."}

eDrafter RESPONSE [27c62798] 201 Created for POST /api/v1/orders in 38ms
  body: {"message":"Order created successfully","orderId":341980,...}
```

Two things are deliberately not printed verbatim: the **API key is masked**, and
**`documentBase64` is summarised** (`<base64 PDF, 96970 chars ≈ 71KB>`) — a whole PDF
would otherwise bury everything else.

| Setting | Default | Purpose |
|---|---|---|
| `EDrafter:LogPayloads` | `true` | Set `false` to silence it |
| `EDrafter:LogMaxBodyChars` | `4000` | Cap on each logged body |

A call that never gets a response logs `eDrafter FAILED` and reminds you that a spending
call in that state is **UNKNOWN**: reconcile, never retry.

### Diagnosing a failed request

Run the API with output to a file, then search it:

```bash
dotnet run --project EDrafter.Api --launch-profile api > api.log 2>&1

grep -E "eDrafter (REQUEST|RESPONSE|FAILED)" api.log      # every call
grep -B 2 -A 3 "eDrafter RESPONSE \[.*\] 4" api.log        # only failures
```

**`401` or `403`** means the key was rejected — every call will fail until it is fixed.
Note the key circulated in a Word document was flagged for rotation, so it may be disabled.

---

## Verification

With all three services running:

```bash
python tests-verify-api.py           # 30 checks — backend flow, both spends, guards
python tests-verify-idempotency.py   #  5 checks — 8 concurrent orders → 1 charge
python tests-verify-e2e.py           # 27 checks — full chain, webhook-driven
cd EDrafter.Api && python ../tests-verify-unknown.py   # 9 checks — timeout → reconcile

cd edrafter-ui && node ../tests-drive-ui.js   # drives the real UI in a browser
```

**All currently pass: 15 (mock) + 30 + 5 + 9 + 27 = 86 checks.**

---

## What the mock simulates faithfully

- **Pricing**, including that **GST never touches stamp duty**, and that the 10%
  high-denomination charge **replaces** the flat ₹55 rather than adding to it
- **`POST /esign` debits at link generation**, per signatory, **no GST**, **no refund on
  decline** — the most important billing behaviour to get right
- **Webhooks with real HMAC-SHA256**, delivered **twice on purpose** so handler
  idempotency is exercised rather than assumed
- 409 on a consumed stamp; 400 when `phone_otp` has no phone
- The async wait, compressed to seconds (`Mock:OrderCompletionSeconds`)

Mock-only controls (no auth, under `/__mock`): `state`, `orders/{idd}/complete`,
`esign/{id}/sign-all`, `esign/{id}/decline?email=…`, `reset`.

---

## Build order

| # | Step | Status |
|---|---|---|
| 1 | Mock eDrafter server | ✅ done |
| 2 | .NET API: client, spend ledger, guard, DB | ✅ done |
| 3 | Angular form + cost gates + live status | ✅ done |
| 4 | Agreement PDF generation | ✅ done |
| 5 | Webhook receiver + HMAC + SignalR + polling | ✅ done |
| 6 | Full end-to-end against the mock | ✅ done |
| 7 | **One live run** (~₹139) | ⬜ **blocked** — see below |

**Nothing in steps 1–6 touches the real API.**

### Before step 7 can happen

| Blocker | Why |
|---|---|
| **Wallet not funded** | eDrafter have not supplied bank/UPI details |
| **API key rotation** | The current key was circulated in a document |
| **The ₹500 cap (S1)** | Not returned by eDrafter's API; confirm before relying on it |
| **Is `email_otp` valid?** | ₹10 vs ₹12 per signatory. Defaulting to `phone_otp` |

---

## Notes on choices

**SQLite by default, PostgreSQL ready.** Neither Postgres nor Docker is installed on this
machine. The schema and every spend-safety guarantee are identical; only the provider
changes:

```jsonc
"Database": { "Provider": "Postgres" },
"ConnectionStrings": { "Default": "Host=localhost;Database=edrafter;Username=…;Password=…" }
```

**Signature placement is cosmetic on all but one page.** `signaturePage` and
`signaturePosition` are top-level fields in eDrafter's API, not per signatory, so the
binding signature lands on **one** page. We draw visible signature blocks on every page so
the document reads as signed throughout, and the footer says plainly where the binding
signature actually is.

**We do not merge the stamp ourselves.** `POST /esign` takes `orderId` + `stampId`
alongside `documentBase64` and eDrafter merges it. Doing it on our side too would put two
stamps on the document.

**Lato, not Calibri.** QuestPDF bundles Lato, so the PDF renders identically anywhere —
including a Linux server with no fonts installed.
