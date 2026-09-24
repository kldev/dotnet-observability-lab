#!/bin/sh
# One-shot Rootprint bootstrap:
#   1. create the first admin (skipped if it already exists),
#   2. sign in,
#   3. create an ingest API key for the OpenTelemetry logs index,
#   4. write it to /bootstrap/rootprint-ingest-key for the OpenTelemetry Collector.
# Re-running is safe: an existing key is kept if Rootprint still accepts it.
set -eu
RP="${ROOTPRINT_URL:-http://rootprint:8282}"
ORIGIN="${ROOTPRINT_ORIGIN:-http://localhost:8282}"
KEY_FILE=/bootstrap/rootprint-ingest-key
JSON='content-type: application/json'

echo "Waiting for Rootprint at $RP..."
until curl -sf -o /dev/null "$RP/api/health"; do sleep 2; done

if [ -s "$KEY_FILE" ]; then
  # An empty body is a valid (empty) OTLP ExportLogsServiceRequest.
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$RP/v1/logs" \
    -H 'content-type: application/x-protobuf' -H "authorization: Bearer $(cat "$KEY_FILE")" --data-binary '')
  if [ "$code" = "200" ]; then echo "Existing ingest key is valid"; exit 0; fi
  echo "Existing ingest key rejected (HTTP $code), creating a new one"
fi

code=$(curl -s -o /tmp/setup -w '%{http_code}' -X POST "$RP/api/auth/setup-admin" -H "$JSON" -H "origin: $ORIGIN" \
  -d "{\"name\":\"Lab Admin\",\"email\":\"$ROOTPRINT_ADMIN_EMAIL\",\"password\":\"$ROOTPRINT_ADMIN_PASSWORD\"}")
case "$code" in
  201) echo "Admin $ROOTPRINT_ADMIN_EMAIL created" ;;
  409) echo "Admin already exists" ;;
  *) echo "setup-admin failed with HTTP $code"; cat /tmp/setup; exit 1 ;;
esac

code=$(curl -s -D /tmp/signin-headers -o /tmp/signin -w '%{http_code}' -X POST "$RP/api/auth/sign-in/email" -H "$JSON" -H "origin: $ORIGIN" \
  -d "{\"email\":\"$ROOTPRINT_ADMIN_EMAIL\",\"password\":\"$ROOTPRINT_ADMIN_PASSWORD\"}")
[ "$code" = "200" ] || { echo "sign-in failed with HTTP $code"; cat /tmp/signin; exit 1; }
# curl's cookie jar ignores cookies for dotless hosts like "rootprint", so pass the session cookie by hand.
SESSION=$(sed -n 's/^set-cookie: \(better-auth[^;]*\);.*/\1/ip' /tmp/signin-headers | head -1)

code=$(curl -s -H "cookie: $SESSION" -o /tmp/key -w '%{http_code}' -X POST "$RP/api/api-keys" -H "$JSON" -H "origin: $ORIGIN" \
  -d "{\"name\":\"observability-lab-$(date +%Y%m%d-%H%M%S)\",\"indexId\":\"otel-logs-v0_9\"}")
[ "$code" = "201" ] || { echo "API key creation failed with HTTP $code"; cat /tmp/key; exit 1; }

sed -n 's/.*"token":"\([^"]*\)".*/\1/p' /tmp/key | tr -d '\n' > "$KEY_FILE"
chmod 644 "$KEY_FILE"
[ -s "$KEY_FILE" ] || { echo "Could not parse token"; cat /tmp/key; exit 1; }
echo "Ingest key written to $KEY_FILE"
