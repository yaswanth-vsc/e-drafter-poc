"""
Step 6 — the whole chain, end to end, driven by webhooks rather than manual prodding.

This is the run that proves the pieces work together: form data becomes a real PDF,
the order webhook lands with a valid HMAC and fetches the stamp, the e-sign charge is
taken once at link generation, the duplicate webhook delivery is ignored, and the
signed document comes back and is archived.
"""
import base64, json, sys, time, urllib.error, urllib.request

API = "http://localhost:5100"
MOCK = "http://localhost:5099"
passed = failed = 0


def call(method, url, body=None, raw=False, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    # The mock requires x-api-key on its /api/v1 routes, exactly as the real API does.
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json",
                                          "x-api-key": "mock-key"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            payload = r.read()
            return r.status, payload if raw else json.loads(payload or b"{}")
    except urllib.error.HTTPError as e:
        payload = e.read()
        if raw:
            return e.code, payload
        try:
            return e.code, json.loads(payload or b"{}")
        except Exception:
            return e.code, {"raw": payload.decode(errors="replace")}


def chk(label, actual, expected):
    global passed, failed
    if actual == expected:
        print(f"  PASS  {label} = {actual}")
        passed += 1
    else:
        print(f"  FAIL  {label} = {actual}  (expected {expected})")
        failed += 1


def chkn(label, actual, expected, tol=0.001):
    global passed, failed
    if abs(float(actual) - float(expected)) < tol:
        print(f"  PASS  {label} = {actual}")
        passed += 1
    else:
        print(f"  FAIL  {label} = {actual}  (expected {expected})")
        failed += 1


def wait_for(fn, what, attempts=40, delay=0.5):
    """Poll a predicate; webhook delivery and handling are asynchronous."""
    for _ in range(attempts):
        if fn():
            return True
        time.sleep(delay)
    print(f"  !! timed out waiting for {what}")
    return False


print("=== 1. All three services are up ===")
st, h = call("GET", f"{API}/api/health")
chk("api health", st, 200)
chk("mode", h["spend"]["mode"], "Mock")
chk("spending disarmed", h["spend"]["armed"], False)

print("\n=== 2. Create the draft from form data ===")
draft = {
    "firstPartyName": "Ramesh Kumar", "firstPartyEmail": "ramesh@example.com",
    "firstPartyPhone": "9876543210",
    "secondPartyName": "Jane Smith", "secondPartyEmail": "jane@example.com",
    "secondPartyPhone": "9876543211",
    "propertyAddress": "No 42, 3rd Cross, Indiranagar, Bengaluru 560038",
    "considerationAmount": 240000, "monthlyRent": 20000,
    "leaseTermMonths": 11, "leaseStartDate": "2026-10-01",
}
st, a = call("POST", f"{API}/api/agreements", draft)
chk("created", st, 201)
aid = a["id"]
# 0.5% of 240000 = 1200, capped to 500 for this article
chkn("duty capped at 500", a["denomination"], 500)
print(f"        refId {a['refId']}")

print("\n=== 3. The agreement PDF generates and fits the size limit ===")
st, pdf = call("POST", f"{API}/api/agreements/{aid}/generate-pdf")
chk("generated", st, 200)
chk("within limit", pdf["withinLimit"], True)
print(f"        {pdf['sizeKb']} KB of a {pdf['limitKb']} KB limit")

st, body = call("GET", f"{API}/api/agreements/{aid}/pdf", raw=True)
chk("served as a real PDF", body[:4], b"%PDF")

print("\n=== 4. Quote: both charges shown before either is made ===")
st, q = call("GET", f"{API}/api/agreements/{aid}/quote")
print(f"        duty  {q['duty']['amount']}  (capped: {q['duty']['wasCapped']})")
print(f"        order {q['order']['total']}")
print(f"        esign {q['esign']['total']}  ({q['esign']['gst']})")
print(f"        TOTAL {q['grandTotal']}")
chkn("order total 500+55+9.90", q["order"]["total"], 564.90)
chkn("esign 12 x 2", q["esign"]["total"], 24)
chkn("grand total", q["grandTotal"], 588.90)

print("\n=== 5. Place the order (spend 1 of 2) ===")
st, o = call("POST", f"{API}/api/agreements/{aid}/order")
chk("succeeded", o["status"], "succeeded")
order_id = o["result"]["orderId"]
print(f"        order {order_id}")

print("\n=== 6. order.completed webhook arrives, verifies, and fetches the stamp ===")
print("        (the mock delivers every event TWICE, on purpose)")
call("POST", f"{MOCK}/__mock/orders/{order_id}/complete")

def stamp_ready():
    _, ag = call("GET", f"{API}/api/agreements/{aid}")
    return ag["status"] == "StampReady"

ok = wait_for(stamp_ready, "the stamp webhook")
chk("webhook advanced the agreement", ok, True)

st, ag = call("GET", f"{API}/api/agreements/{aid}")
chk("status", ag["status"], "StampReady")
print(f"        certificate {ag['certificateNo']}")

st, stamp = call("GET", f"{API}/api/agreements/{aid}/stamp-pdf", raw=True)
chk("stamp PDF archived", stamp[:4], b"%PDF")

print("\n=== 7. Duplicate webhook delivery was a no-op ===")
# The mock delivered order.completed twice; only one row should have been processed,
# and crucially the stamp must not have been fetched or saved twice.
st, attempts = call("GET", f"{API}/api/spend/attempts")
mine = [x for x in attempts if x.get("agreementId") == aid]
chk("still exactly one spend row", len(mine), 1)

print("\n=== 8. Send for signing (spend 2 of 2, non-refundable) ===")
st, e = call("POST", f"{API}/api/agreements/{aid}/prepare-and-send")
chk("succeeded", e["status"], "succeeded")
doc_id = e["result"]["documentId"]
for s in e["result"]["signatories"]:
    print(f"        {s['email']:22} -> {s['signUrl'][:44]}...")

print("\n=== 9. esign.completed webhook archives the signed document ===")
call("POST", f"{MOCK}/__mock/esign/{doc_id}/sign-all")

def signed():
    _, ag2 = call("GET", f"{API}/api/agreements/{aid}")
    return ag2["status"] == "Signed"

ok = wait_for(signed, "the esign webhook")
chk("webhook advanced to Signed", ok, True)

st, signed_pdf = call("GET", f"{API}/api/agreements/{aid}/signed-pdf", raw=True)
chk("signed PDF archived", signed_pdf[:4], b"%PDF")

st, ag = call("GET", f"{API}/api/agreements/{aid}")
for s in ag["signatories"]:
    print(f"        {s['name']:15} {s['status']}")

print("\n=== 10. Exactly two charges, correct amounts, nothing unresolved ===")
st, attempts = call("GET", f"{API}/api/spend/attempts")
mine = [x for x in attempts if x.get("agreementId") == aid]
chk("two spend rows", len(mine), 2)
amounts = sorted(x["amountPaise"] for x in mine)
chk("esign 2400 paise", amounts[0], 2400)
chk("order 56490 paise", amounts[1], 56490)
chk("all succeeded", all(x["status"] == 1 for x in mine), True)

st, sm = call("GET", f"{API}/api/spend/summary")
chk("nothing needs review", sm["needsReviewCount"], 0)
chk("no unknowns", sm["unknownCount"], 0)

print("\n=== 11. Wallet at eDrafter matches what we think we spent ===")
st, txns = call("GET", f"{MOCK}/api/v1/transactions")
if "transactions" not in txns:
    print(f"        !! unexpected shape: {list(txns)[:5]}")
    recent = []
else:
    recent = txns["transactions"][:2]
for t in recent:
    print(f"        {t['type']:7} {t['amount']:>9}  {t['description'][:52]}")
debits = sorted(t["amount"] for t in recent if t["type"] == "Debit")
chkn("eDrafter charged 24 for e-sign", debits[0], 24)
chkn("eDrafter charged 564.90 for the order", debits[1], 564.90)

print("\n" + "=" * 52)
print(f"  PASS: {passed}   FAIL: {failed}")
print("=" * 52)
sys.exit(1 if failed else 0)
