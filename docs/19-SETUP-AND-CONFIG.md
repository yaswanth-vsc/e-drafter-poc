# Setup, configuration, and webhooks

How to run the POC, what every setting does, and how to register a webhook.

For what the app actually does, see [18-THE-FLOW.md](18-THE-FLOW.md).

---

## What you need

| | |
|---|---|
| .NET SDK | 10.0 (project targets `net10.0`) |
| Node.js | 22.22.3+ or 24.15+ for Angular tooling; **v24.11 works** with Angular 19 |
| Python | 3.8+ — only for the verification scripts |
| PostgreSQL | **not required.** SQLite is the default |

---

## Running it

**Two terminals against the real eDrafter. Three if you use the mock.**
Order matters: the API calls eDrafter at startup, and the UI calls the API.

### Terminal 1 — the backend

```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
dotnet run --project EDrafter.Api --launch-profile api
```

Wait for `Now listening on: http://localhost:5100`.

### Terminal 2 — the UI

```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src\edrafter-ui
npx ng serve
```

Open **http://localhost:4200**.

### Optional — the mock eDrafter

Only when you want to exercise the **spending** calls for free. Start it **before** the
backend, and point `EDrafter:BaseUrl` at it.

```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
dotnet run --project EDrafter.MockServer --launch-profile mock
```

---

## Configuration

Everything lives in
[`src/EDrafter.Api/appsettings.json`](../src/EDrafter.Api/appsettings.json). Any value can
be overridden by an environment variable using `__` for nesting:

```bash
EDrafter__ArmSpending=false dotnet run --project EDrafter.Api --launch-profile api
```

### The two that control spending

| Setting | Meaning |
|---|---|
| **`EDrafter:Mode`** | `Live` calls the real eDrafter. `Mock` skips every guard, since nothing real can be spent. |
| **`EDrafter:ArmSpending`** | The safety catch. `false` refuses `POST /orders` and `POST /esign` with a message naming the exact amount. **Pointing at the live API is not enough to spend.** |

Four combinations, only one of which costs money:

| Mode | ArmSpending | Clicking "Place order" | Money lost |
|---|---|---|---|
| Mock | either | works, fake money | ₹0 |
| **Live** | **false** | **BLOCKED** | **₹0** |
| Live | true | real order | **real ₹** |

### eDrafter connection

| Setting | Current | Notes |
|---|---|---|
| `BaseUrl` | `https://edrafterb2b.in/api/v1` | `http://localhost:5099/api/v1` for the mock |
| `ApiKey` | *(64 chars)* | ⚠️ In production this belongs in Key Vault, not a committed file |
| `TimeoutSeconds` | 30 | A timeout on a spending call is the UNKNOWN case |
| `MaxTotalSpendPaise` | **0** | Running-total ceiling. **0 disables it.** |
| `LogPayloads` | true | Logs every request and response in full |
| `LogMaxBodyChars` | 4000 | Cap per logged body |

### Scope — what the POC handles

```jsonc
"Scope": { "State": "Karnataka", "ArticleCode": "30(1)(i)", "Quantity": 1 }
```

### Stamp duty

```jsonc
"DutyRules": { "30(1)(i)": { "Pct": 0.5, "CapRupees": 500 } }
```

🚨 **The cap is per-article, not global.** eDrafter's API returns `unconstrained` for this
article — no percentage, no cap — so **nothing validates the duty but us**. An article
with no entry here gets no local cap; its limits come from `GET /states/:state/rules`.

### e-Sign

```jsonc
"EsignRatesPaise": { "email_otp": 1000, "phone_otp": 1200, "dsc": 1500, "aadhaar_otp": 2000 },
"Esign": {
  "DefaultSignMethod": "phone_otp",
  "SignaturePosition":  "bottom-left",
  "ExpiryDays":         7,
  "Signatories":        "second"     // "both" | "first" | "second"
}
```

Rates are in **paise** and carry **no GST**. `Signatories` is a cost lever: `second`
charges for one signatory, `both` charges for two.

`email_otp` is cheapest at ₹10 but is **not a documented `signMethod`** — their API lists
only `phone_otp`, `aadhaar_otp`, `dsc`. Untested.

### Database

```jsonc
"Database": { "Provider": "Sqlite" },
"ConnectionStrings": { "Default": "Data Source=edrafter-poc.db" }
```

For PostgreSQL — the schema and every guarantee are identical:

```jsonc
"Database": { "Provider": "Postgres" },
"ConnectionStrings": { "Default": "Host=localhost;Database=edrafter;Username=…;Password=…" }
```

---

## Starting from a clean slate

The database and the stored PDFs persist between runs. Stop the backend first.

```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
rm -f EDrafter.Api/edrafter-poc.db
rm -rf EDrafter.Api/storage
```

---

## Webhooks

### Why you need a tunnel

eDrafter delivers webhooks by making an HTTP request **to you**. They cannot reach
`http://localhost:5100` — on their servers, "localhost" is their own machine. A tunnel
gives yours a temporary public address:

```
eDrafter  →  https://xyz.loca.lt  →  your machine  →  localhost:5100
```

### The order that works

🚨 **Backend → tunnel → register.** Restarting the backend **kills the tunnel**, which
forces a new URL, a new secret, and another restart. That loop cost real time; this order
avoids it.

**1. Start the backend**, pointed at a secret file rather than a fixed value:

```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
EDrafter__WebhookSecretFile="$TEMP/whsec_live.txt" \
EDrafter__Webhooks__AutoRegister=false \
dotnet run --project EDrafter.Api --launch-profile api
```

The file is read **per request**, so re-registering later needs no restart.

**2. Start the tunnel** — only once the backend is up:

```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
npm install                    # once
npx localtunnel --port 5100
```

Note the URL it prints, e.g. `https://twenty-eggs-cheat.loca.lt`.

**3. Register it** and write the secret to the file:

```bash
node register-webhook.js \
  --base https://edrafterb2b.in/api/v1 \
  --key  YOUR_API_KEY \
  --url  https://twenty-eggs-cheat.loca.lt/webhooks/edrafter
```

Paste the printed secret into `$TEMP/whsec_live.txt`. **No restart needed.**

Or do steps 2 and 3 in one go:

```bash
npm run tunnel:register -- --key YOUR_API_KEY
```

### Checking and cleaning up

```bash
node register-webhook.js --base https://edrafterb2b.in/api/v1 --key KEY --list
node register-webhook.js --base https://edrafterb2b.in/api/v1 --key KEY --delete <id>
```

**Delete stale hooks.** A dead URL means eDrafter keeps retrying an address that no longer
exists.

### Against the mock

Nothing to do — the backend registers itself at startup and captures the secret, provided
`Webhooks:AutoRegister` is left at its default.

### Four things that will bite you

| | |
|---|---|
| **The secret is shown once** | eDrafter returns it at creation and never again. Lose it → delete the hook and register a new one. |
| **The URL changes on every restart** | localtunnel issues a new subdomain each time, so the registered hook is instantly dead. |
| **The tunnel drops silently** | It returned 503 mid-session more than once. If nothing arrives, check the tunnel *before* blaming eDrafter. |
| **Only three events exist** | No webhook for e-sign decline, expiry, individual signatures, or order rejection. Polling covers those. |

---

## Reading the logs

Every eDrafter call and every webhook is logged in full, paired by a correlation id.

```bash
dotnet run --project EDrafter.Api --launch-profile api > api.log 2>&1
```

**Outgoing:**

```
eDrafter REQUEST  [70f0945b] GET https://edrafterb2b.in/api/v1/orders/104197
  headers: x-api-key=098a…ea (64 chars)          ← masked
  body: (empty)

eDrafter RESPONSE [70f0945b] 200 OK in 32ms
  body: {"_idd":104197,"status":"Completed",...}
```

**Incoming:**

```
WEBHOOK IN  [7a39c20c] event=order.completed from=::1
  signature: sha256=eba37e51…
  body: {"event":"order.completed","data":{"orderId":104197,...}}

WEBHOOK OK  [7a39c20c] signature=verified processed=True duplicate=False
```

> `from=::1` is normal — deliveries arrive through the tunnel, which forwards from
> localhost. **The source IP does not tell you whether a delivery was genuine; the payload
> does.**

```bash
grep -E "eDrafter (REQUEST|RESPONSE)|WEBHOOK" api.log      # everything
grep -E "RESPONSE \[.*\] [45]|WEBHOOK REJECTED" api.log    # only failures
```

The API key is masked and `documentBase64` is summarised, so logs are safe to share.

---

## Verifying it works

With the backend and the **mock** running:

```bash
cd C:\Users\yaswanth.k\Desktop\eDrafter\src
python tests-verify-api.py           # 30 checks — flow, both spends, guards
python tests-verify-idempotency.py   #  5 checks — 8 concurrent orders → 1 charge
python tests-verify-e2e.py           # 27 checks — full chain, webhook-driven
cd EDrafter.Api && python ../tests-verify-unknown.py   # 9 checks — timeout → reconcile
```

Drive the real UI in a browser:

```bash
cd edrafter-ui && node ../tests-drive-ui.js
```

---

## Troubleshooting

**"Cannot reach the API. Is it running on :5100?"**
The UI is up, the backend is not. Start terminal 1.

**"The file is locked by EDrafter.Api"** on build
An older copy is still running:

```bash
netstat -ano | grep LISTENING | grep ':5100 '
taskkill //PID <pid> //F
```

**"BLOCKED: … ArmSpending is false"**
Working as designed. Set `ArmSpending: true` only when you intend to spend.

**A webhook never arrives**
Check in this order: is the tunnel alive (`curl` its `/api/health`); is the hook still
registered and pointing at the *current* URL; did the backend restart since the tunnel
started. Note `order.cancelled` has never fired — that one is eDrafter's.

**The UI does not update by itself**
SignalR is live-only, with no replay. If the page was closed or the backend restarted when
the event fired, the push is simply lost. **Reload** — the page re-fetches state on load.

---

## Before production

| | |
|---|---|
| **API key** | Move to Key Vault. It is currently in a committed file. |
| **Webhook secret** | Same. The file-based store is for local tunnelling only. |
| **Tunnel** | Replace with a real public host. |
| **Database** | Switch to PostgreSQL. |
| **Spend ceiling** | Currently disabled. Re-enable with a sensible cap. |
| **`ArmSpending`** | Keep `false` in every checked-in config. |
| **`certificateNo`** | Empty on this test account. Verify it populates on a live one. |
