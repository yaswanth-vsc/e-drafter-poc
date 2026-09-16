"""
The scenario the ledger exists for: POST /orders times out.

The order MAY have been placed. The client cannot tell. A naive retry double-charges.
What must happen instead:
  1. The attempt is recorded as UNKNOWN, not as a failure.
  2. The API refuses to retry and says so explicitly.
  3. POST /api/reconcile looks the order up by OUR refId (free, read-only)
     and adopts it when found — no second spend.
"""
import json, urllib.request, urllib.error, sys, sqlite3, os, time

API = "http://localhost:5100/api"
MOCK = "http://localhost:5099"
passed = failed = 0

def call(method, url, body=None, timeout=30):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:    return e.code, json.loads(raw or b"{}")
        except Exception: return e.code, {"raw": raw.decode(errors="replace")}

def chk(label, actual, expected):
    global passed, failed
    if actual == expected:
        print(f"  PASS  {label} = {actual}"); passed += 1
    else:
        print(f"  FAIL  {label} = {actual}  (expected {expected})"); failed += 1

DB = os.path.join(os.environ.get("EDRAFTER_DB_DIR", "."), "edrafter-poc.db")

print("=== Simulating an UNKNOWN outcome ===")
print("An order IS placed at eDrafter, but we force our ledger row to UNKNOWN —")
print("exactly the state a timeout leaves behind: the spend happened, we don't know it.\n")

draft = {
    "firstPartyName": "Timeout Test", "firstPartyEmail": "tt@example.com", "firstPartyPhone": "9000000003",
    "secondPartyName": "Other Party", "secondPartyEmail": "op@example.com", "secondPartyPhone": "9000000004",
    "propertyAddress": "Unknown Outcome Lane, Bengaluru",
    "considerationAmount": 10000, "monthlyRent": 9000,
    "leaseTermMonths": 11, "leaseStartDate": "2026-12-01"
}
st, a = call("POST", f"{API}/agreements", draft)
aid, refid = a["id"], a["refId"]
print(f"  agreement {aid}\n  refId     {refid}")

st, o = call("POST", f"{API}/agreements/{aid}/order")
chk("order placed at eDrafter", st, 200)
real_order_id = o["result"]["orderId"]
print(f"  order {real_order_id} exists at eDrafter — the money HAS moved")

# Force the ledger row to UNKNOWN and the agreement to NeedsReview, as a timeout would.
if not os.path.exists(DB):
    print(f"  !! database not found at {DB}; set EDRAFTER_DB_DIR")
    sys.exit(2)

conn = sqlite3.connect(DB)
conn.execute("UPDATE SpendAttempts SET Status=3, EdrafterId=NULL, FailureReason='Timeout: simulated' "
             "WHERE IdempotencyKey=?", (f"order:{aid}",))
conn.execute("UPDATE Agreements SET Status=9 WHERE Id=?", (aid,))
conn.commit(); conn.close()
print("  ledger row forced to UNKNOWN, agreement to NeedsReview\n")

print("=== 1. The UNKNOWN is visible, not hidden as a failure ===")
st, sm = call("GET", f"{API}/spend/summary")
chk("unknown count >= 1", sm["unknownCount"] >= 1, True)
print(f"        needsReview={sm['needsReviewCount']}")

print("\n=== 2. Retrying is REFUSED — no second charge ===")
st, retry = call("POST", f"{API}/agreements/{aid}/order")
chk("retry refused", st in (403, 409), True)
print(f"        {retry.get('status')}: {retry.get('message','')[:76]}")

_, before = call("GET", f"{MOCK}/__mock/state")
print(f"        orders at eDrafter before reconcile: {before['orders']}")

print("\n=== 3. Reconcile finds the real order by OUR refId and adopts it ===")
st, rec = call("POST", f"{API}/reconcile")
mine = [r for r in rec if r["message"] and refid in str(r.get("message",""))]
for r in rec:
    print(f"        attempt {r['attemptId']}  resolved={r['resolved']}")
    print(f"          {r['message'][:96]}")
chk("at least one reconciled", any(r["resolved"] for r in rec), True)

_, after = call("GET", f"{MOCK}/__mock/state")
chk("NO new order created by reconcile", after["orders"], before["orders"])

print("\n=== 4. Agreement is back in step with reality ===")
st, ag = call("GET", f"{API}/agreements/{aid}")
chk("status recovered to OrderPlaced", ag["status"], "OrderPlaced")
chk("adopted the real order id", ag["orderIdd"], real_order_id)

print("\n=== 5. Ledger shows Adopted, and still exactly one row ===")
st, attempts = call("GET", f"{API}/spend/attempts")
mine = [x for x in attempts if x.get("agreementId") == aid]
chk("one ledger row", len(mine), 1)
chk("status = Adopted (4)", mine[0]["status"], 4)
print(f"        edrafterId={mine[0]['edrafterId']}  amount={mine[0]['amountPaise']} paise")

print("\n" + "=" * 46)
print(f"  PASS: {passed}   FAIL: {failed}")
print("=" * 46)
sys.exit(1 if failed else 0)
