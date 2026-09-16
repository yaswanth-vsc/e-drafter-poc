"""
Proves the LEDGER itself blocks a duplicate charge — not just the status check.

The status guard in AgreementService refuses a second order because the agreement has
moved past Draft. That is a good first line, but it is application logic. The claim that
matters is stronger: even with concurrent requests racing, the UNIQUE index on
idempotency_key means eDrafter is called exactly once.
"""
import json, urllib.request, urllib.error, threading, sqlite3, os, sys

API = "http://localhost:5100/api"
MOCK = "http://localhost:5099"
passed = failed = 0

def call(method, url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req) as r:
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

draft = {
    "firstPartyName": "Concurrent Test", "firstPartyEmail": "ct@example.com", "firstPartyPhone": "9000000001",
    "secondPartyName": "Second Party",   "secondPartyEmail": "sp@example.com", "secondPartyPhone": "9000000002",
    "propertyAddress": "Race Condition Road, Bengaluru",
    "considerationAmount": 10000, "monthlyRent": 8000,
    "leaseTermMonths": 11, "leaseStartDate": "2026-11-01"
}

print("=== Concurrent duplicate order: 8 threads, same agreement, fired together ===")
st, a = call("POST", f"{API}/agreements", draft)
aid = a["id"]
print(f"  agreement {aid}")

# Count orders at the mock before and after, so we measure what eDrafter actually saw.
_, before = call("GET", f"{MOCK}/__mock/state")
orders_before = before["orders"]

results = []
lock = threading.Lock()
barrier = threading.Barrier(8)

def fire():
    barrier.wait()                       # release all threads at the same instant
    st, body = call("POST", f"{API}/agreements/{aid}/order")
    with lock:
        results.append((st, body.get("status")))

threads = [threading.Thread(target=fire) for _ in range(8)]
for t in threads: t.start()
for t in threads: t.join()

_, after = call("GET", f"{MOCK}/__mock/state")
orders_after = after["orders"]

ok = sum(1 for st, _ in results if st == 200)
refused = sum(1 for st, _ in results if st in (403, 409))

print(f"  responses: {ok} succeeded, {refused} refused")
for st, status in sorted(results):
    print(f"    HTTP {st}  {status}")

chk("exactly one succeeded", ok, 1)
chk("all others refused", refused, 7)
chk("orders actually created at eDrafter", orders_after - orders_before, 1)

print("\n=== Ledger holds exactly one attempt for this agreement ===")
st, attempts = call("GET", f"{API}/spend/attempts")
mine = [x for x in attempts if x.get("agreementId") == aid]
chk("ledger rows for this agreement", len(mine), 1)
if mine:
    r = mine[0]
    print(f"    key={r['idempotencyKey']}  status={r['status']}  amount={r['amountPaise']} paise")
    chk("idempotency key is derived, not random", r["idempotencyKey"], f"order:{aid}")

print("\n" + "=" * 46)
print(f"  PASS: {passed}   FAIL: {failed}")
print("=" * 46)
sys.exit(1 if failed else 0)
