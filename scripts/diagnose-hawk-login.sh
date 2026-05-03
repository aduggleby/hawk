#!/usr/bin/env bash

set -euo pipefail

BASE_URL="${BASE_URL:-http://alex-office-nas.ohana:17800}"
WEB_CONTAINER="${WEB_CONTAINER:-ix-hawk-hawk-1}"
PROTECTED_PATH="${PROTECTED_PATH:-/monitors}"
LOG_SINCE="${LOG_SINCE:-10m}"
OUT="${OUT:-hawk-login-diagnose-$(date +%Y%m%d-%H%M%S).log}"

if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required." >&2
  exit 1
fi

if ! command -v sudo >/dev/null 2>&1; then
  echo "sudo is required." >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required." >&2
  exit 1
fi

read -r -p "Login email: " LOGIN_EMAIL
read -r -s -p "Login password: " LOGIN_PASSWORD
echo

if [[ -z "${LOGIN_EMAIL}" || -z "${LOGIN_PASSWORD}" ]]; then
  echo "Email and password are required." >&2
  exit 1
fi

TMPDIR="$(mktemp -d)"
trap 'rm -rf "$TMPDIR"' EXIT

COOKIE_JAR="$TMPDIR/cookies.txt"
LOGIN_PAGE_HEADERS="$TMPDIR/login-page.headers"
LOGIN_PAGE_BODY="$TMPDIR/login-page.html"
POST_HEADERS="$TMPDIR/post.headers"
POST_BODY="$TMPDIR/post.body"
PROTECTED_HEADERS="$TMPDIR/protected.headers"
PROTECTED_BODY="$TMPDIR/protected.body"

section() {
  local title="$1"
  {
    echo
    echo "===== $title ====="
  } >>"$OUT"
}

append_file() {
  local file="$1"
  if [[ -f "$file" ]]; then
    cat "$file" >>"$OUT"
  fi
}

extract_status_code() {
  local file="$1"
  awk 'toupper($1) ~ /^HTTP\// { code=$2 } END { print code }' "$file"
}

extract_location() {
  local file="$1"
  awk 'BEGIN{IGNORECASE=1} /^Location:/ { sub(/\r$/, "", $2); print $2; exit }' "$file"
}

extract_request_verification_token() {
  local file="$1"
  sed -n 's/.*name="__RequestVerificationToken".*value="\([^"]*\)".*/\1/p' "$file" | head -n 1
}

contains_invalid_login_message() {
  local file="$1"
  grep -qi "Invalid login attempt" "$file"
}

{
  echo "Started at $(date -Is)"
  echo "BASE_URL=$BASE_URL"
  echo "WEB_CONTAINER=$WEB_CONTAINER"
  echo "PROTECTED_PATH=$PROTECTED_PATH"
  echo "LOG_SINCE=$LOG_SINCE"
  echo "HOST=$(hostname)"
} >"$OUT"

curl -sS \
  -D "$LOGIN_PAGE_HEADERS" \
  -o "$LOGIN_PAGE_BODY" \
  -c "$COOKIE_JAR" \
  "${BASE_URL}/Identity/Account/Login?ReturnUrl=%2Fmonitors"

TOKEN="$(extract_request_verification_token "$LOGIN_PAGE_BODY")"
if [[ -z "$TOKEN" ]]; then
  echo "Could not extract __RequestVerificationToken from login page." | tee -a "$OUT" >&2
  section "login page headers"
  append_file "$LOGIN_PAGE_HEADERS"
  section "login page body"
  append_file "$LOGIN_PAGE_BODY"
  exit 1
fi

curl -sS \
  -D "$POST_HEADERS" \
  -o "$POST_BODY" \
  -b "$COOKIE_JAR" \
  -c "$COOKIE_JAR" \
  -X POST \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "Input.Email=${LOGIN_EMAIL}" \
  --data-urlencode "Input.Password=${LOGIN_PASSWORD}" \
  --data-urlencode "Input.RememberMe=false" \
  --data-urlencode "__RequestVerificationToken=${TOKEN}" \
  "${BASE_URL}/Identity/Account/Login?ReturnUrl=%2Fmonitors"

POST_STATUS="$(extract_status_code "$POST_HEADERS")"
POST_LOCATION="$(extract_location "$POST_HEADERS" || true)"

curl -sS \
  -D "$PROTECTED_HEADERS" \
  -o "$PROTECTED_BODY" \
  -b "$COOKIE_JAR" \
  "${BASE_URL}${PROTECTED_PATH}"

PROTECTED_STATUS="$(extract_status_code "$PROTECTED_HEADERS")"
PROTECTED_LOCATION="$(extract_location "$PROTECTED_HEADERS" || true)"

section "login page headers"
append_file "$LOGIN_PAGE_HEADERS"

section "login post headers"
append_file "$POST_HEADERS"

section "cookie jar"
append_file "$COOKIE_JAR"

section "protected request headers"
append_file "$PROTECTED_HEADERS"

section "recent hawk auth logs"
sudo docker logs --since "$LOG_SINCE" "$WEB_CONTAINER" 2>&1 \
  | grep -E "User logged in|Invalid login attempt|User account locked out|Auth challenge" \
  >>"$OUT" || true

section "app auth-related env"
sudo docker exec "$WEB_CONTAINER" sh -lc \
  "printenv | grep -E '^(ASPNETCORE_|DOTNET_RUNNING_IN_CONTAINER|Hawk__DisableHttpsRedirection|Hawk__DataProtection__KeysPath)=' || true" \
  >>"$OUT" 2>&1 || true

section "diagnosis"
if contains_invalid_login_message "$POST_BODY"; then
  {
    echo "RESULT=INVALID_CREDENTIALS"
    echo "DETAIL=The login page returned an invalid login attempt message."
  } >>"$OUT"
elif [[ "$POST_STATUS" != "302" ]]; then
  {
    echo "RESULT=UNEXPECTED_LOGIN_RESPONSE"
    echo "DETAIL=Expected login POST to redirect, got HTTP ${POST_STATUS:-unknown}."
  } >>"$OUT"
elif ! grep -Eq $'\\t(Hawk.Auth|\\.AspNetCore\\.Identity\\.Application)\\t' "$COOKIE_JAR"; then
  {
    echo "RESULT=NO_AUTH_COOKIE_ISSUED"
    echo "DETAIL=Login POST redirected but no auth cookie was stored by curl."
  } >>"$OUT"
elif [[ "$PROTECTED_STATUS" == "302" && "$PROTECTED_LOCATION" == *"/Identity/Account/Login"* ]]; then
  {
    echo "RESULT=AUTH_COOKIE_REJECTED_ON_NEXT_REQUEST"
    echo "DETAIL=Login POST issued a cookie, but the protected page still redirected back to login."
  } >>"$OUT"
else
  {
    echo "RESULT=LOGIN_WORKS"
    echo "DETAIL=Protected request succeeded after login."
  } >>"$OUT"
fi

{
  echo
  echo "POST_STATUS=${POST_STATUS:-}"
  echo "POST_LOCATION=${POST_LOCATION:-}"
  echo "PROTECTED_STATUS=${PROTECTED_STATUS:-}"
  echo "PROTECTED_LOCATION=${PROTECTED_LOCATION:-}"
} >>"$OUT"

echo
echo "Wrote $OUT"
