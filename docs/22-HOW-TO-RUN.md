# How to run

e-Stamp from **eDrafter**, e-signature from **Zoho Sign** (Aadhaar eSign). Everything runs on
your machine; the browser talks only to our API.

| Piece | Folder | Port |
|---|---|---|
| API (.NET 10) | `src/EDrafter.Api` | 5100 |
| UI (Angular) | `src/edrafter-ui` | 4200 |
| Mock eDrafter (optional, free) | `src/EDrafter.MockServer` | 5099 |

---

## 1. Before the first run

**Install:** .NET 10 SDK · Node.js 18+ · Git Bash (for the commands below).

```bash
cd src/edrafter-ui && npm install      # once
```

**Credentials** live in `src/EDrafter.Api/appsettings.Local.json`. Git ignores it — never put
secrets in `appsettings.json`, which is committed. It already has:

| Section | What | Current value |
|---|---|---|
| `EDrafter` | live eDrafter URL, API key, webhook secret file | **Live, `ArmSpending: true`** |
| `Zoho` | client id / secret / refresh token (copied from the Zoho POC) | `TestMode: true`, `ArmSpending: true` |

> `appsettings.Local.json` is loaded **last**, so its values win over `appsettings.json` **and
> over environment variables**. To change a setting, edit this file and restart the API.

---

## 2. What costs money — read this first

| Switch | Effect |
|---|---|
| `EDrafter:ArmSpending` | `true` → **Place order** really buys an e-stamp from the eDrafter wallet (~₹109). `false` → blocked. |
| `Zoho:ArmSpending` | `true` → **Send to Zoho Sign** really submits. `false` → only a free draft is created, the submit is blocked. |
| `Zoho:TestMode` | `true` → the 5-credit send is free (50 test documents a month); the PDF gets a **"For demo purpose only"** watermark and has no legal value. |
| Aadhaar eSign | **2 credits (₹12) per signer, charged even in test mode**, when each person signs. |

With the current settings, one agreement costs:

| | Test mode on (now) | Test mode off |
|---|---|---|
| e-stamp (eDrafter) | ~₹109 | ~₹109 |
| Zoho send | free | 5 credits = ₹30 |
| Aadhaar, 2 signers | 4 credits = ₹24 | 4 credits = ₹24 |

Every spending call is recorded **before** it is made and can never run twice for the same
agreement. A timeout is marked **NeedsReview** — use *Reconcile*, never resend.

---

## 3. Start it

Stop any API that is already running first — port 5100 can only be used once.

```bash
# Terminal 1 — API
cd src
dotnet run --project EDrafter.Api --launch-profile api
```

Check the startup log:

```
eDrafter POC API starting. Mode=Live ArmSpending=True BaseUrl=https://edrafterb2b.in/api/v1
Signing provider=zoho. Zoho: … Credentials=set ArmSpending=True Providers=[25] … WebhookSecret=not set
```

`Credentials=set` means the Zoho login is in place. `Providers=[25]` means Aadhaar eSign only.

```bash
# Terminal 2 — UI
cd src/edrafter-ui
npx ng serve
```

Open **http://localhost:4200**.

Optional — the free mock eDrafter (see [21-WEBHOOKS.md](21-WEBHOOKS.md) §2 for switching to it):

```bash
# Terminal 3
cd src
dotnet run --project EDrafter.MockServer --launch-profile mock
```

Optional — webhooks need a tunnel; see [21-WEBHOOKS.md](21-WEBHOOKS.md). Without one, everything
still works through the *check now* / *refresh signing status* buttons and the 10-minute polling.

---

## 4. The flow in the UI

1. **Fill the form** — first party, second party (name, email, phone), property, rent, term,
   consideration.
2. **Review the quote** — eDrafter's breakdown: stamp duty, service charge, GST, total, wallet
   balance. Zoho's cost is not in the quote.
3. **Place order** (tick the box first) — 💸 buys the e-stamp.
4. **Wait for the stamp** — eDrafter processes it manually, about 1 working hour (Mon–Fri 9–6,
   cut-off 3:30pm). The webhook or *check now* moves it to **StampReady** and saves the stamp PDF.
5. **Check the layout** — *Preview the signature boxes* shows the stamp paper + agreement with both
   boxes on every page. Built on your machine, sends nothing.
6. **Send to Zoho Sign** (tick the box first) — uploads the PDF with the boxes, second party first,
   Aadhaar only.
7. **Signing** — the second party gets the email, signs with one Aadhaar OTP (all pages). Then the
   first party gets the email and signs.
8. **Signed** — the webhook or *refresh signing status* downloads the signed PDF; *download signed
   PDF* appears.

---

## 5. Useful endpoints

All on `http://localhost:5100`. Only the ones marked 💸 can cost money.

| Endpoint | What it does |
|---|---|
| `GET /api/health` | eDrafter account + wallet, spend summary, Zoho config (does not call Zoho) |
| `GET /api/agreements` · `GET /api/agreements/{id}` | list / one agreement |
| `GET /api/agreements/{id}/quote` | eDrafter quote (free) |
| `POST /api/agreements/{id}/order` | 💸 place the e-stamp order |
| `POST /api/agreements/{id}/refresh-stamp` | check whether the stamp is ready (free) |
| `GET /api/agreements/{id}/signing-preview` | final PDF with the signature boxes drawn (local) |
| `GET /api/agreements/{id}/final-pdf` | the exact file that goes to Zoho (local) |
| `GET /api/agreements/{id}/signing-layout` | pages, sizes and box coordinates as JSON (local) |
| `POST /api/agreements/{id}/prepare-and-send` | 💸 send to Zoho Sign |
| `POST /api/agreements/{id}/refresh-signing` | read the Zoho status; downloads the signed PDF when complete (free) |
| `POST /api/agreements/{id}/zoho/reset-draft` | forget an **unsent** Zoho draft so the next send starts fresh (local) |
| `GET /api/agreements/{id}/stamp-pdf` · `/signed-pdf` | the stamp as issued · the signed document |
| `POST /api/reconcile` | settle a spend whose outcome is unknown (reads only, never resends) |

---

## 6. Where things are saved

```
src/EDrafter.Api/
  edrafter-poc.db        agreements, signers, spend ledger, webhook de-duplication
  storage/stamps/        e-stamp PDFs as issued by eDrafter
  storage/final/         stamp + agreement, as uploaded to Zoho (+ previews)
  storage/signed/        signed PDFs from Zoho
```

New database columns are added automatically on start. To start completely clean, stop the API and
delete `edrafter-poc.db*` and `storage/` — this also forgets every stamp you have paid for.

---

## 7. When something goes wrong

| Problem | Fix |
|---|---|
| `address already in use` on start | Another API is running on 5100 — stop it. |
| Log says `Credentials=MISSING` | The Zoho section in `appsettings.Local.json` is missing or misspelled. |
| Send says `BLOCKED … ArmSpending is false` | Intended safety stop. A free Zoho draft was created; check it in Zoho, then set `Zoho:ArmSpending: true` and send again — the same draft is reused. |
| Boxes in the wrong place on the Zoho draft | Delete the draft in Zoho, call `zoho/reset-draft`, adjust `Signing:StampPage*` or `Zoho:CoordinateMode`, send again. |
| Status **NeedsReview** | A spending call timed out. Click *Reconcile* — never send again by hand. |
| Stamp or signature never arrives | Click *check now* / *refresh signing status*; see [21-WEBHOOKS.md](21-WEBHOOKS.md). |
| Zoho token error (`invalid_code`) | The refresh token was revoked — mint a new one with the Zoho POC's `mint-refresh-token.ps1`. |

More detail: [19-SETUP-AND-CONFIG.md](19-SETUP-AND-CONFIG.md) (every eDrafter setting) ·
[21-WEBHOOKS.md](21-WEBHOOKS.md).
