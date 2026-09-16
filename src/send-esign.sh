#!/usr/bin/env bash
#
# POST /esign with a PDF attached.
#
# 💸 SPENDS MONEY, AND IT IS NOT REFUNDABLE.
# The wallet is debited when the signing link is GENERATED, per signatory — not when
# signing completes. A decline, a cancellation or an expiry keeps the charge.
#
# It also CONSUMES the stamp permanently. A second attempt on the same stamp returns 409.
#
# Usage:
#   export KEY="your_api_key"
#   ./send-esign.sh agreement.pdf 104069 1
#
set -euo pipefail

PDF="${1:?usage: ./send-esign.sh <pdf> <orderId> <stampId>}"
ORDER_ID="${2:?missing orderId}"
STAMP_ID="${3:?missing stampId}"
: "${KEY:?set KEY first:  export KEY=\"your_api_key\"}"

BASE="https://edrafterb2b.in/api/v1"

SIGNATORY_NAME="yaswanth"
SIGNATORY_EMAIL="yaswant.k@vsoftwareconsulting.com"
SIGNATORY_PHONE="7386083380"
SIGN_METHOD="phone_otp"          # phone_otp Rs.12 | aadhaar_otp Rs.20 | dsc Rs.15

[ -f "$PDF" ] || { echo "No such file: $PDF" >&2; exit 1; }

# eDrafter caps the body at ~1MB. Base64 inflates by a third, so the real PDF limit is
# about 512KB. Check BEFORE sending: exceeding it fails the call, and on a spending
# endpoint you would rather find out for free.
BYTES=$(wc -c < "$PDF")
LIMIT=$((512 * 1024))
echo "PDF      : $PDF  ($((BYTES / 1024))KB of a $((LIMIT / 1024))KB limit)"
if [ "$BYTES" -gt "$LIMIT" ]; then
  echo "TOO LARGE. Compress it first - this would fail after the wallet was debited." >&2
  exit 1
fi

# -w0 keeps it on one line; a base64 with newlines breaks the JSON.
B64=$(base64 -w0 "$PDF")

echo "order    : $ORDER_ID  stamp $STAMP_ID"
echo "signatory: $SIGNATORY_NAME <$SIGNATORY_EMAIL> $SIGNATORY_PHONE"
echo "method   : $SIGN_METHOD"
echo
echo "This charges for 1 signatory and is NOT refundable."
echo "It also consumes stamp $ORDER_ID/$STAMP_ID permanently."
read -r -p "Type SEND to continue: " confirm
[ "$confirm" = "SEND" ] || { echo "Aborted. Nothing spent."; exit 0; }

# Build the body with a heredoc so the base64 never goes through shell quoting.
BODY=$(cat <<JSON
{
  "name": "Lease Agreement - Karnataka 30(1)(i)",
  "signMethod": "$SIGN_METHOD",
  "orderId": $ORDER_ID,
  "stampId": $STAMP_ID,
  "documentName": "$(basename "$PDF")",
  "documentBase64": "$B64",
  "signatories": [
    {
      "name": "$SIGNATORY_NAME",
      "email": "$SIGNATORY_EMAIL",
      "phone": "$SIGNATORY_PHONE"
    }
  ],
  "reason": "Lease agreement execution",
  "expiryDays": 7,
  "signaturePosition": "bottom-left"
}
JSON
)

echo
echo "Sending..."
curl -sS -X POST "$BASE/esign" \
  -H "x-api-key: $KEY" \
  -H "Content-Type: application/json" \
  -w "\nHTTP %{http_code}\n" \
  -d "$BODY"
