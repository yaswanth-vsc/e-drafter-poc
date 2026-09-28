# Webhooks — eDrafter and Zoho Sign

How to register both webhooks, and how to test them on your own machine.

Both arrive at the same backend (`http://localhost:5100`), on different paths:

| Provider | Our endpoint | Events we use | Registered by |
|---|---|---|---|
| **eDrafter** | `POST /webhooks/edrafter` | `order.completed`, `order.cancelled`, `esign.completed` | API call (script) |
| **Zoho Sign** | `POST /webhooks/zoho-sign` | Sent, Viewed, Signed, Completed, Declined, Expired, Recalled, Reassigned | Zoho web UI only |

**Webhooks are a speed-up, not a requirement.** If a webhook never arrives, the polling service
(every 10 minutes) and the *check now* / *refresh signing status* buttons reach the same result.

---

## 1. Why you need a tunnel

eDrafter and Zoho deliver a webhook by calling **your** server. From their servers,
`http://localhost:5100` means *their own machine*, so they cannot reach you. A tunnel gives your
machine a temporary public `https` address:

```
eDrafter / Zoho  →  https://abc123.ngrok-free.app  →  your machine  →  localhost:5100
```

One tunnel serves both webhooks, because both paths are on port 5100.

| Tunnel | Setup | Notes |
|---|---|---|
| **ngrok** (recommended) | free account + authtoken | Stable while it runs; works for Zoho |
| **localtunnel** | none (`npm i` in `src/`) | Already set up for eDrafter; new URL on every start; has dropped with 503 mid-session |

```bash
# ngrok
ngrok http 5100                       # prints https://<something>.ngrok-free.app

# localtunnel (from src/)
npx localtunnel --port 5100           # prints https://<something>.loca.lt
```

🚨 **Order matters: backend → tunnel → register.** Restarting the backend does not change an
ngrok URL, but restarting the *tunnel* usually does — and then both registrations point at a dead
address and must be updated.

---

## 2. eDrafter webhook

### Register

eDrafter shows the signing secret **only once**, when the webhook is created. The scripts print it
and tell you where to put it.

```bash
cd src

# see what is registered
node register-webhook.js --base https://edrafterb2b.in/api/v1 --key <API_KEY> --list

# register the tunnel URL
node register-webhook.js --base https://edrafterb2b.in/api/v1 --key <API_KEY> \
  --url https://<tunnel>/webhooks/edrafter

# remove an old one
node register-webhook.js --base https://edrafterb2b.in/api/v1 --key <API_KEY> --delete <id>
```

Or open a localtunnel and register it in one go (stays running):

```bash
npm run tunnel:register -- --key <API_KEY>
```

Put the secret where the backend reads it. Your `appsettings.Local.json` already points at a
secret **file** (`EDrafter:WebhookSecretFile`); write the new secret into that file. The file is
re-read on every delivery, so no restart is needed. (`EDrafter:WebhookSecret` in config, if set,
wins over the file.)

### How eDrafter signs

| | |
|---|---|
| Header | `X-eDrafter-Signature: sha256=<lowercase hex>` and `X-eDrafter-Event: <event>` |
| Algorithm | HMAC-SHA256 of the **raw body**, keyed with the webhook secret |
| Retries | Up to 5 times — the same event **will** arrive twice; the second is ignored |
| No secret known | The check is skipped with a warning (local use only) |

Payload shape:

```json
{ "event": "order.completed", "data": { "orderId": 104197 }, "attempt": 1 }
```

### Test locally — option A: the mock eDrafter (no money, no tunnel)

The mock plays eDrafter on port 5099, registers the webhook automatically at startup, and fires
real signed webhooks — including a deliberate duplicate.

1. In `src/EDrafter.Api/appsettings.Local.json` set:
   ```json
   "EDrafter": { "BaseUrl": "http://localhost:5099/api/v1", "Mode": "Mock", "ArmSpending": false, ... }
   ```
   and remove `Webhooks:AutoRegister: false` (auto-register is on by default in Mock mode).
   > Values in `appsettings.Local.json` win over environment variables, because the app loads that
   > file last — so edit the file rather than overriding with env vars.
2. Start the mock, then the API (see [22-HOW-TO-RUN.md](22-HOW-TO-RUN.md)).
3. Create an agreement and place the order in the UI.
4. Complete the order in the mock, which fires `order.completed`:
   ```bash
   curl -X POST http://localhost:5099/__mock/orders/<orderIdd>/complete
   ```
5. The API log shows `WEBHOOK IN … signature=verified`, then a duplicate no-op; the UI moves to
   **StampReady**.

Switch `appsettings.Local.json` back to the live values afterwards.

### Test locally — option B: send a signed webhook yourself (Git Bash)

Useful for checking the endpoint and signature without eDrafter at all.

```bash
SECRET='<the eDrafter webhook secret>'
BODY='{"event":"order.completed","data":{"orderId":104197},"attempt":1}'
SIG="sha256=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" | sed 's/^.* //')"

curl -i -X POST http://localhost:5100/webhooks/edrafter \
  -H "Content-Type: application/json" \
  -H "X-eDrafter-Event: order.completed" \
  -H "X-eDrafter-Signature: $SIG" \
  --data "$BODY"
```

- `200 … "processed":true` — accepted. Send it again: `"duplicate":true`.
- `401` — the signature did not match: wrong secret, or the body was changed.
- ⚠️ For an order id we track, `order.completed` makes the API **fetch and download the stamp from
  live eDrafter** (a free read — but downloading marks the stamp *Used*). Use an id we do not track
  to test only the signature path.

---

## 3. Zoho Sign webhook

### Register — web UI only

Zoho has no API for this.

1. Sign in to Zoho Sign (**sign.zoho.in**) as an admin.
2. **Settings → Developer settings → Webhooks → Create webhook.**
3. URL: `https://<tunnel>/webhooks/zoho-sign`
4. Events: **Sent, Viewed, Signed by a recipient, Completed by all, Declined, Recalled, Expires,
   Reassigned.**
5. Turn on the **secret / HMAC** option and copy the secret — the full string, including the
   `whsec_` prefix. Zoho shows it once.
6. Put it in `src/EDrafter.Api/appsettings.Local.json` → `"Zoho": { "WebhookSecret": "whsec_…" }`
   and restart the API.

> The Zoho account has **one** webhook list shared by everything using it. If the old Zoho POC's
> webhook is still registered, events for our requests go there too — our endpoint ignores
> requests it does not track, and the POC ignores ours.

### How Zoho signs

| | |
|---|---|
| Header | `X-ZS-WEBHOOK-SIGNATURE: <base64>` (optional `X-ZS-WEBHOOK-TIMESTAMP`, epoch seconds) |
| Algorithm | HMAC-SHA256 of the **raw body**, **base64** (not hex), keyed with the whole `whsec_…` string |
| Replay guard | A timestamp older than 5 minutes is rejected |
| No secret configured | Accepted **unverified** with a warning — `Zoho:WebhookSecret` is empty today |
| Zoho's "Test Url" button | Sends an **unsigned** sample. Set `Zoho:WebhookAllowUnsigned: true` to let it through while testing |

Our endpoint always answers **200** once a delivery is authenticated — Zoho disables a webhook
after repeated failures.

What each event does:

| Zoho `operation_type` | Our status |
|---|---|
| `RequestSubmitted` (Sent) | stays SentForSigning |
| `RequestViewed` | no change |
| `RequestSigningSuccess` | **PartiallySigned**, that signer marked signed |
| `RequestCompleted` | **Signed**, signed PDF downloaded to `storage/signed/` |
| `RequestRejected` / `RequestExpired` | **Failed** (credits not refunded) |
| `RequestRecalled` | **Cancelled** |
| `RequestForwarded` (Reassigned) | no change, **warning** logged — the signer no longer matches the stamp |

### Test locally — send a signed Zoho webhook yourself (Git Bash)

```bash
SECRET='whsec_...'           # Zoho:WebhookSecret; if it is empty, the signature is not checked
REQ='184612000000084001'     # a ZohoRequestId the app tracks (see GET /api/agreements)
BODY=$(cat <<EOF
{"requests":{"request_id":"$REQ","request_status":"inprogress","actions":[
  {"action_id":"x1","recipient_email":"sreenivasulu.v@vsoftwareconsulting.com","action_status":"SIGNED"},
  {"action_id":"x2","recipient_email":"yaswanth.k@vsoftwareconsulting.com","action_status":"UNOPENED"}]},
 "notifications":{"operation_type":"RequestSigningSuccess","performed_by_email":"sreenivasulu.v@vsoftwareconsulting.com","performed_at":1790445743824}}
EOF
)
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -binary | base64)

curl -i -X POST http://localhost:5100/webhooks/zoho-sign \
  -H "Content-Type: application/json" \
  -H "X-ZS-WEBHOOK-SIGNATURE: $SIG" \
  --data "$BODY"
```

- `200 … "processed":true` — the agreement moves to **PartiallySigned**. The same body again →
  `"processed":false` (duplicate).
- `"processed":false` on the first try — the `request_id` is not one we track.
- `401` — signature mismatch.
- ⚠️ `RequestCompleted` for a tracked request makes the API **download the signed PDF from Zoho**
  (a free read, no credits). Everything else here is local.

### Test end to end with Zoho

With the tunnel up and the webhook registered, send one agreement for signing (test mode) and watch
the API log for `ZOHO WEBHOOK IN` as each person signs. Remember: **Aadhaar eSign costs 2 credits
per signer even in test mode** — only the 5-credit send is free.

---

## 4. Troubleshooting

| Symptom | Check, in this order |
|---|---|
| Nothing arrives | Is the tunnel alive? `curl https://<tunnel>/api/health`. Is the registered URL the *current* tunnel URL? |
| `401` in our log | Wrong secret. eDrafter: the secret file / `EDrafter:WebhookSecret`. Zoho: `Zoho:WebhookSecret`, including `whsec_`. |
| Zoho "Test Url" fails | It is unsigned — set `Zoho:WebhookAllowUnsigned: true` while testing. |
| `processed:false` | Duplicate delivery (normal), or an order / request id we do not track. |
| Status stuck | Use *check now* / *refresh signing status*, or wait for the 10-minute poll. |

Related: [19-SETUP-AND-CONFIG.md](19-SETUP-AND-CONFIG.md) (more eDrafter tunnel detail) ·
[22-HOW-TO-RUN.md](22-HOW-TO-RUN.md) (running it).
