"""Backend verification. Every assertion is a spend-safety guarantee we depend on."""
import json, urllib.request, urllib.error, base64, sys

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

def chkn(label, actual, expected, tol=0.001):
    global passed, failed
    if abs(float(actual) - float(expected)) < tol:
        print(f"  PASS  {label} = {actual}"); passed += 1
    else:
        print(f"  FAIL  {label} = {actual}  (expected {expected})"); failed += 1

print("=== 1. Health: API reaches eDrafter, spend guard reports Mock/unarmed ===")
st, h = call("GET", f"{API}/health")
chk("health status", st, 200)
chk("mode", h["spend"]["mode"], "Mock")
chk("armed", h["spend"]["armed"], False)
print(f"        wallet ₹{h['edrafter']['balance']}")

print("\n=== 2. Rules come from the API, not hardcoded ===")
st, r = call("GET", f"{API}/rules")
chk("rules status", st, 200)
chk("article", r["article"], "30(1)(i)")
chk("state", r["state"], "Karnataka")
print(f"        denominationConstraint.max = {r['rules']['denominationConstraint']['max']}  <- no cap from API (S1)")

print("\n=== 3. Draft: duty = 0.5% of consideration ===")
draft = {
    "firstPartyName": "Ramesh Kumar", "firstPartyEmail": "ramesh@example.com", "firstPartyPhone": "9876543210",
    "secondPartyName": "Jane Smith",  "secondPartyEmail": "jane@example.com",  "secondPartyPhone": "9876543211",
    "propertyAddress": "No 42, 3rd Cross, Indiranagar, Bengaluru 560038",
    "considerationAmount": 10000, "monthlyRent": 10000,
    "leaseTermMonths": 11, "leaseStartDate": "2026-10-01"
}
st, a = call("POST", f"{API}/agreements", draft)
chk("created", st, 201)
aid = a["id"]
chkn("duty on ₹10,000 (0.5%)", a["denomination"], 50)
chk("status", a["status"], "Draft")
print(f"        refId = {a['refId']}")

print("\n=== 4. The ₹500 cap applies (per-article, config-driven) ===")
big = dict(draft); big["considerationAmount"] = 1000000   # 0.5% = 5000, capped to 500
st, a2 = call("POST", f"{API}/agreements", big)
chkn("duty on ₹10,00,000 capped", a2["denomination"], 500)

print("\n=== 5. Quote is free and shows BOTH costs before any spend ===")
st, q = call("GET", f"{API}/agreements/{aid}/quote")
chk("quote status", st, 200)
print(f"        duty        ₹{q['duty']['amount']}  (capped: {q['duty']['wasCapped']})")
print(f"        order total ₹{q['order']['total']}  (stamp {q['order']['stampValue']} + svc {q['order']['serviceCharge']} + gst {q['order']['gst']})")
print(f"        esign       ₹{q['esign']['total']}  = ₹{q['esign']['ratePerSignatory']} x {q['esign']['signatories']}  [{q['esign']['gst']}]")
print(f"        GRAND TOTAL ₹{q['grandTotal']}")
chkn("order total", q["order"]["total"], 114.90)
chkn("esign total (12 x 2, no GST)", q["esign"]["total"], 24)
chkn("grand total", q["grandTotal"], 138.90)

print("\n=== 6. Place the order (SPENDS via ledger) ===")
st, o = call("POST", f"{API}/agreements/{aid}/order")
chk("order status", st, 200)
chk("outcome", o["status"], "succeeded")
print(f"        orderId {o['result']['orderId']}  total ₹{o['result']['total']}")

print("\n=== 7. DOUBLE-CHARGE GUARD: ordering again is refused ===")
st, dup = call("POST", f"{API}/agreements/{aid}/order")
chk("second order refused (403 or 409)", st in (403, 409), True)
print(f"        {dup.get('status')}: {dup.get('message','')[:78]}")

print("\n=== 8. Stamp becomes available ===")
oid = o["result"]["orderId"]
call("POST", f"{MOCK}/__mock/orders/{oid}/complete")
st, s = call("POST", f"{API}/agreements/{aid}/refresh-stamp")
chk("stamp attached", s["stampReady"], True)
st, ag = call("GET", f"{API}/agreements/{aid}")
chk("status now StampReady", ag["status"], "StampReady")
print(f"        certificate {ag['certificateNo']}")

print("\n=== 9. Send for signing (SPENDS at link generation) ===")
pdf_b64 = base64.b64encode(b"%PDF-1.4 fake agreement body").decode()
st, e = call("POST", f"{API}/agreements/{aid}/send-for-signing", {"documentBase64": pdf_b64})
chk("send status", st, 200)
chk("outcome", e["status"], "succeeded")
for sig in e["result"]["signatories"]:
    print(f"        {sig['email']:22} -> {sig['signUrl'][:46]}...")

print("\n=== 10. DOUBLE-CHARGE GUARD on e-sign: sending again is refused ===")
st, dup2 = call("POST", f"{API}/agreements/{aid}/send-for-signing", {"documentBase64": pdf_b64})
chk("second send -> 403/409", st in (403, 409), True)
print(f"        {dup2.get('status')}: {dup2.get('message','')[:78]}")

print("\n=== 11. Ledger records exactly two spends, no duplicates ===")
st, attempts = call("GET", f"{API}/spend/attempts")
mine = [x for x in attempts if x.get("agreementId") == aid]
kinds = [(x["kind"], x["status"], x["amountPaise"]) for x in mine]
chk("attempt count for this agreement", len(mine), 2)
for k, stt, amt in kinds:
    kind_name = {0: "Order", 1: "Esign"}.get(k, k)
    status_name = {0: "Attempting", 1: "Succeeded", 2: "FailedSafe", 3: "Unknown", 4: "Adopted"}.get(stt, stt)
    print(f"        {kind_name:6} {status_name:11} {amt} paise (₹{amt/100:.2f})")
amounts = sorted(x[2] for x in kinds)
chk("esign debit paise", amounts[0], 2400)
chk("order debit paise", amounts[1], 11490)
chk("all succeeded", all(x[1] == 1 for x in kinds), True)

print("\n=== 12. Signing completes; status reflects it ===")
st, ag = call("GET", f"{API}/agreements/{aid}")
docid = ag["esignDocumentId"]
call("POST", f"{MOCK}/__mock/esign/{docid}/sign-all")
call("POST", f"{API}/agreements/{aid}/refresh-signing")
st, ag = call("GET", f"{API}/agreements/{aid}")
chk("status now Signed", ag["status"], "Signed")
for sig in ag["signatories"]:
    print(f"        {sig['name']:15} {sig['status']}")

print("\n=== 13. Spend summary totals correctly ===")
st, sm = call("GET", f"{API}/spend/summary")
mine_total = sum(x["amountPaise"] for x in attempts if x.get("agreementId") == aid)
chkn("this agreement's spend ₹", mine_total / 100, 138.90)
chk("unknown count", sm["unknownCount"], 0)
print(f"        mode={sm['mode']} armed={sm['armed']} ceiling=₹{sm['ceilingPaise']/100}")

print("\n=== 14. Reconcile is a no-op when nothing is unresolved ===")
st, rec = call("POST", f"{API}/reconcile")
chk("nothing to reconcile", len(rec), 0)

print("\n" + "=" * 46)
print(f"  PASS: {passed}   FAIL: {failed}")
print("=" * 46)
sys.exit(1 if failed else 0)
