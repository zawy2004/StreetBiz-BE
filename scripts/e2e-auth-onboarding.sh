#!/usr/bin/env bash
# End-to-end check of Authentication (AUTH-01..09) and Vendor Onboarding (REG-01..05)
# against a running StreetBiz-BE. Creates two throwaway vendor accounts per run.
#
# Usage (Git Bash, from StreetBiz-BE):
#   terminal 1:  dotnet run --project src/StreetBiz.API 2>&1 | tee api.log
#   terminal 2:  bash scripts/e2e-auth-onboarding.sh
#
# LOG (default api.log) must be the API console output: the dev SMS sender prints
# OTP codes there. Use Git Bash's tee — PowerShell 5.1's Tee-Object writes UTF-16.
# The ward-review leg signs in as a seeded WARD_AUTHORITY account for ward 10
# (db/StreetBiz_Demo_Seed.sql); override with WARD_PHONE / WARD_PW.
API=${API:-http://localhost:5023/api}
LOG=${LOG:-api.log}
[ -f "$LOG" ] || { echo "Không thấy $LOG. Chạy API trong Git Bash: dotnet run --project src/StreetBiz.API 2>&1 | tee api.log"; exit 2; }
J="Content-Type: application/json"
PASS=0; FAIL=0
TMP=$(mktemp -d)

json() { node -pe "const o=JSON.parse(require('fs').readFileSync(0,'utf8')); $1"; }
check() { # name expected actual
  if [ "$2" = "$3" ]; then PASS=$((PASS+1)); echo "  PASS  $1 ($3)"; else FAIL=$((FAIL+1)); echo "  FAIL  $1 expected=[$2] got=[$3]"; fi; }
req() { # method path [token] [body] -> sets CODE, BODY
  local m=$1 p=$2 t=$3 b=$4
  local args=(-s -o "$TMP/body" -w "%{http_code}" -X "$m" "$API$p")
  [ -n "$t" ] && args+=(-H "Authorization: Bearer $t")
  [ -n "$b" ] && args+=(-H "$J" -d "$b")
  CODE=$(curl "${args[@]}"); BODY=$(cat "$TMP/body"); }
otp_for() { sleep 1; grep -o "To $1: Your StreetBiz verification code is [0-9]\{6\}" "$LOG" | tail -1 | grep -o '[0-9]\{6\}$'; }
up() { CODE=$(curl -s -o "$TMP/body" -w "%{http_code}" -H "Authorization: Bearer $1" -F "file=@$2" "$API/uploads/evidence"); BODY=$(cat "$TMP/body"); }

STAMP=$(date +%s | tail -c 9)
PHONE="09$STAMP"; P2="08$STAMP"; PW='Str0ng!Pass'
# Ward review needs a real WARD_AUTHORITY account; db/StreetBiz_Demo_Seed.sql seeds these.
WARD_PHONE=${WARD_PHONE:-0983000001}; WARD_PW=${WARD_PW:-Password123!}
ORIGIN=${API%/api}

echo "== Reference data"
req GET /administrative-units/wards; check "list wards" 200 "$CODE"

echo "== AUTH-02 / AUTH-01 register"
req POST /auth/send-otp "" "{\"phoneNumber\":\"$PHONE\",\"purpose\":\"REGISTRATION\"}"; check "send OTP" 200 "$CODE"
req POST /auth/send-otp "" "{\"phoneNumber\":\"$PHONE\",\"purpose\":\"REGISTRATION\"}"; check "resend inside cooldown -> 429" 429 "$CODE"
check "429 carries retryAfterSeconds" true "$(echo "$BODY" | json 'o.retryAfterSeconds>0')"
OTP=$(otp_for "$PHONE")
req POST /auth/register "" "{\"phoneNumber\":\"$PHONE\",\"password\":\"$PW\",\"fullName\":\"E2E\",\"roleCode\":\"VENDOR\",\"wardUnitId\":999,\"otp\":\"$OTP\"}"
check "register with unknown ward -> 400 (not 500)" 400 "$CODE"
check "error keyed WardUnitId" true "$(echo "$BODY" | json '"WardUnitId" in o.errors')"
req POST /auth/register "" "{\"phoneNumber\":\"$PHONE\",\"password\":\"$PW\",\"fullName\":\"E2E\",\"roleCode\":\"VENDOR\",\"wardUnitId\":10,\"otp\":\"$OTP\"}"
check "register (OTP still valid after ward error)" 200 "$CODE"
T1=$(echo "$BODY" | json o.accessToken); R1=$(echo "$BODY" | json o.refreshToken)
req POST /auth/send-otp "" "{\"phoneNumber\":\"$PHONE\",\"purpose\":\"REGISTRATION\"}"; check "OTP for already-registered phone -> 409" 409 "$CODE"

echo "== AUTH-03 login / AUTH-05 reset uniformity"
req POST /auth/login "" "{\"phoneNumber\":\"$PHONE\",\"password\":\"wrong\"}"; check "wrong password" 401 "$CODE"
req POST /auth/login "" "{\"phoneNumber\":\"$PHONE\",\"password\":\"$PW\"}"; check "login" 200 "$CODE"
req POST /auth/forgot-password "" "{\"phoneNumber\":\"$PHONE\"}"; check "forgot (registered)" 200 "$CODE"
req POST /auth/forgot-password "" "{\"phoneNumber\":\"$PHONE\"}"; check "forgot again: no cooldown leak" 200 "$CODE"
req POST /auth/send-otp "" "{\"phoneNumber\":\"$PHONE\",\"purpose\":\"PASSWORD_RESET\"}"; check "send-otp reset: no cooldown leak" 200 "$CODE"
req POST /auth/forgot-password "" "{\"phoneNumber\":\"0999999999\"}"; check "forgot (unknown phone) same answer" 200 "$CODE"

echo "== AUTH-03 sign in with OTP instead of a password (FE-01, BR-03)"
req POST /auth/send-otp "" "{\"phoneNumber\":\"0999999999\",\"purpose\":\"LOGIN\"}"
check "login code for unknown phone: same answer" 200 "$CODE"
req POST /auth/send-otp "" "{\"phoneNumber\":\"$PHONE\",\"purpose\":\"LOGIN\"}"; check "request login code" 200 "$CODE"
LOTP=$(otp_for "$PHONE")
req POST /auth/login-otp "" "{\"phoneNumber\":\"$PHONE\",\"otp\":\"000000\"}"; check "wrong login code -> 401" 401 "$CODE"
req POST /auth/login-otp "" "{\"phoneNumber\":\"$PHONE\",\"otp\":\"$LOTP\"}"; check "sign in with the code" 200 "$CODE"
check "OTP sign-in returns a session" true "$(echo "$BODY" | json 'o.accessToken.length>20')"
req POST /auth/login-otp "" "{\"phoneNumber\":\"$PHONE\",\"otp\":\"$LOTP\"}"; check "the code is single use -> 401" 401 "$CODE"

echo "== CR-06 the +84 form is the same account"
req POST /auth/login "" "{\"phoneNumber\":\"+84${PHONE#0}\",\"password\":\"$PW\"}"
check "+84 form signs in to the same account" 200 "$CODE"
check "stored as the local form" "$PHONE" "$(echo "$BODY" | json o.user.phoneNumber)"

echo "== AUTH-07 change password"
req POST /auth/change-password "$T1" "{\"currentPassword\":\"nope\",\"newPassword\":\"N3w!Passw0rd\"}"
check "wrong current -> 400 (not 401)" 400 "$CODE"
check "error keyed CurrentPassword" true "$(echo "$BODY" | json '"CurrentPassword" in o.errors')"

echo "== Upload (REG-02 files)"
printf '\xFF\xD8\xFF\xE0fakejpegbody' > "$TMP/id.jpg"
printf 'MZ not an image' > "$TMP/evil.jpg"
head -c 6000000 /dev/zero > "$TMP/big.jpg"
up "$T1" "$TMP/id.jpg"; check "upload jpg" 200 "$CODE"; URL=$(echo "$BODY" | json o.fileUrl)
up "$T1" "$TMP/evil.jpg"; check "upload disguised non-image -> 400" 400 "$CODE"
up "$T1" "$TMP/big.jpg"; check "upload over 5 MB rejected (400/413)" true "$( { [ "$CODE" = 400 ] || [ "$CODE" = 413 ]; } && echo true || echo false)"
CODE=$(curl -s -o /dev/null -w "%{http_code}" -F "file=@$TMP/id.jpg" "$API/uploads/evidence"); check "upload without token" 401 "$CODE"

echo "== REG-01 / REG-02 / REG-03"
req POST /vendor/registrations "$T1" '{"vendorType":"FIXED_STOREFRONT","displayName":"E2E Shop","declaredAddress":"1 Le Duan","addressLatitude":16.0678,"addressLongitude":108.2208,"wardUnitId":999}'
check "submit unknown ward -> 400" 400 "$CODE"
req POST /vendor/registrations "$T1" '{"vendorType":"FIXED_STOREFRONT","displayName":"E2E Shop","declaredAddress":"1 Le Duan","addressLatitude":16.0678,"addressLongitude":108.2208,"wardUnitId":10}'
check "submit" 200 "$CODE"; RID=$(echo "$BODY" | json o.data.registrationId)
req POST "/vendor/registrations/$RID/evidence" "$T1" '{"evidenceType":"IDENTITY_DOCUMENT","fileUrl":"blob:http://localhost:5173/x","ocrExtractedData":null}'
check "evidence with blob: URL -> 400" 400 "$CODE"
req POST "/vendor/registrations/$RID/evidence" "$T1" "{\"evidenceType\":\"IDENTITY_DOCUMENT\",\"fileUrl\":\"$URL\",\"ocrExtractedData\":null}"
check "evidence with uploaded URL" 200 "$CODE"
req GET "/vendor/registrations/$RID" "$T1"; check "detail" 200 "$CODE"
check "detail lists evidence" 1 "$(echo "$BODY" | json o.evidence.length)"
check "detail returns coordinates" 16.0678 "$(echo "$BODY" | json o.registration.addressLatitude)"
CODE=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer $T1" "$ORIGIN$URL"); check "owner downloads file" 200 "$CODE"

echo "== Another vendor cannot read or reuse the file"
req POST /auth/send-otp "" "{\"phoneNumber\":\"$P2\",\"purpose\":\"REGISTRATION\"}"; O2=$(otp_for "$P2")
req POST /auth/register "" "{\"phoneNumber\":\"$P2\",\"password\":\"$PW\",\"fullName\":\"Other\",\"roleCode\":\"VENDOR\",\"wardUnitId\":10,\"otp\":\"$O2\"}"
TO=$(echo "$BODY" | json o.accessToken)
CODE=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer $TO" "$ORIGIN$URL"); check "other vendor download -> 403" 403 "$CODE"
req POST /vendor/registrations "$TO" '{"vendorType":"ITINERANT","displayName":"Other","declaredAddress":null,"wardUnitId":10}'
RO=$(echo "$BODY" | json o.data.registrationId)
req POST "/vendor/registrations/$RO/evidence" "$TO" "{\"evidenceType\":\"IDENTITY_DOCUMENT\",\"fileUrl\":\"$URL\",\"ocrExtractedData\":null}"
check "attach someone else's file -> 400" 400 "$CODE"
req GET "/vendor/registrations/$RID" "$TO"; check "read someone else's registration -> 403" 403 "$CODE"

echo "== WARD review of the registration (REG-06 / SRS 3.3.1)"
req POST /auth/login "" "{\"phoneNumber\":\"$WARD_PHONE\",\"password\":\"$WARD_PW\"}"
check "ward officer signs in" 200 "$CODE"; WT=$(echo "$BODY" | json o.accessToken)
req GET "/ward/cases/registrations/$RID" "$WT"; check "officer opens the case" 200 "$CODE"
check "case is waiting for review" SUBMITTED "$(echo "$BODY" | json o.status)"
req GET "/ward/cases/registrations/$RID" "$T1"; check "vendor cannot use the ward queue -> 403" 403 "$CODE"
req POST "/ward/cases/registrations/$RID/decision" "$WT" \
  '{"decision":"REQUEST_INFO","reason":"Bo sung giay phep kinh doanh","expectedStatus":"SUBMITTED"}'
check "officer asks for more evidence" 200 "$CODE"
check "status is MORE_INFORMATION_REQUIRED" MORE_INFORMATION_REQUIRED "$(echo "$BODY" | json o.status)"
req POST "/ward/cases/registrations/$RID/decision" "$WT" \
  '{"decision":"REQUEST_INFO","reason":"Lap lai","expectedStatus":"SUBMITTED"}'
check "stale expectedStatus is rejected -> 409" 409 "$CODE"

echo "== BR-09 on re-submit (REG-04)"
req POST /vendor/registrations "$T1" '{"vendorType":"ITINERANT","displayName":"Second","declaredAddress":null,"wardUnitId":10}'
check "new application allowed while first needs info" 200 "$CODE"; RID2=$(echo "$BODY" | json o.data.registrationId)
req PUT "/vendor/registrations/$RID" "$T1" '{"vendorType":"FIXED_STOREFRONT","displayName":"E2E Shop","declaredAddress":"1 Le Duan","addressLatitude":16.0678,"addressLongitude":108.2208,"wardUnitId":10}'
check "re-submit while another is pending -> 409" 409 "$CODE"
req POST "/vendor/registrations/$RID2/withdraw" "$T1"; check "withdraw the second one" 200 "$CODE"
req PUT "/vendor/registrations/$RID" "$T1" '{"vendorType":"FIXED_STOREFRONT","displayName":"E2E Shop v2","declaredAddress":"1 Le Duan","addressLatitude":16.0678,"addressLongitude":108.2208,"wardUnitId":10}'
check "re-submit now allowed" 200 "$CODE"
check "status back to SUBMITTED" SUBMITTED "$(echo "$BODY" | json o.registrationStatus)"
req POST "/vendor/registrations/$RID2/evidence" "$T1" "{\"evidenceType\":\"OTHER\",\"fileUrl\":\"$URL\",\"ocrExtractedData\":null}"
check "evidence on withdrawn registration -> 422" 422 "$CODE"

echo "== AUTH-09 / AUTH-04: revoked tokens stop working immediately"
req GET /auth/sessions "$T1"; check "list sessions" 200 "$CODE"
req POST /auth/login "" "{\"phoneNumber\":\"$PHONE\",\"password\":\"$PW\"}"; S2=$(echo "$BODY" | json o.accessToken)
SID2=$(node -pe "JSON.parse(Buffer.from(process.argv[1].split('.')[1],'base64url')).sid" "$S2")
req DELETE "/auth/sessions/$SID2" "$T1"; check "revoke other device" 200 "$CODE"
req GET /auth/sessions "$S2"; check "revoked device token rejected" 401 "$CODE"
check "rejection has an empty body (client will refresh)" "" "$BODY"
req POST /auth/logout "$T1"; check "logout" 200 "$CODE"
req GET /auth/sessions "$T1"; check "logged-out access token rejected" 401 "$CODE"
req POST /auth/refresh "" "{\"refreshToken\":\"$R1\"}"; check "logged-out refresh token rejected" 401 "$CODE"

rm -rf "$TMP"
echo; echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
