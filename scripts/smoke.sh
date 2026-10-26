#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5217}"
RABBIT="${RABBIT:-http://127.0.0.1:15707}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }

open_tier() { # code ticketsTotal -> prints nothing
  curl -sf -X POST "$API/api/tiers" -H "$JSON" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Show\",\"section\":\"Floor\",\"venue\":\"Arena Norte\",\"ticketsTotal\":$2,\"priceCents\":5000}" >/dev/null \
    || fail "could not open tier $1"
}
book() { # code reference tickets -> prints order id
  curl -sf -X POST "$API/api/orders" -H "$JSON" \
    -d "{\"tierCode\":\"$1\",\"reference\":\"$2\",\"tickets\":$3}" | jget "['id']"
}
order_field() { curl -sf "$API/api/orders/$1" | jget "['$2']"; }
remaining() { curl -sf "$API/api/tiers/$1" | jget "['ticketsLeft']"; }

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(order_field "$1" status)" = "$2" ]; }
remaining_is() { [ "$(remaining "$1")" = "$2" ]; }
terminal() { case "$(order_field "$1" status)" in Sold|Rejected|Refunded) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf "$API/api/orders/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/etickets.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "1. input is validated at the door"
open_tier "V-$RUN" 10
for body in \
  "{\"tierCode\":\"V-$RUN\",\"reference\":\"\",\"tickets\":1}" \
  "{\"tierCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tickets\":0}" \
  "{\"tierCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tickets\":7}" \
  "{\"tierCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tickets\":4294967297}" \
  "{\"tierCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tickets\":9223372036854775807}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/orders" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing ticket counts are all 400"

echo "2. happy path"
open_tier "H-$RUN" 20
ID="$(book "H-$RUN" "BUYER-$RUN-1" 4)"
wait_for "order to be Sold" status_is "$ID" Sold
[ "$(order_field "$ID" totalCents)" = 22400 ] || fail "total should be 4 x 5000 plus a 12% service fee = 22400"
[ "$(curl -sf "$API/api/e-tickets/$ID" | jget "['status']")" = Issued ] || fail "e-ticket not issued"
wait_for "remaining tickets to be 16" remaining_is "H-$RUN" 16
TYPES="$(event_types "$ID")"
for t in tickets.reserve tickets.held eticket.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "sold, total 22400, remaining 16, Kafka log has the three steps"

echo "3. last ticket: three orders, one commits, two are short and write no hold"
open_tier "L-$RUN" 1
TMP="$(mktemp -d)"
for n in A B C; do ( book "L-$RUN" "BUYER-$RUN-$n" 1 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "order $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "order $i to settle" terminal "$i"; done
SOLD=0; SHORT=0
for i in $IDS; do
  s="$(order_field "$i" status)"
  if [ "$s" = Sold ]; then SOLD=$((SOLD + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(order_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *tickets.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$SOLD" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 sold and 2 short, got $SOLD sold and $SHORT short"
[ "$(remaining "L-$RUN")" = 0 ] || fail "remaining should be 0"
say "1 sold, 2 short, remaining 0, no hold for the losers"

echo "4. poison: committed as rejected, dead-lettered, the tickets come back"
open_tier "P-$RUN" 5
BEFORE="$(dlq_count)"
IDP="$(book "P-$RUN" "BUYER-POISON-$RUN" 2)"
wait_for "poison order to be Rejected" status_is "$IDP" Rejected
wait_for "tickets to be restored to 5" remaining_is "P-$RUN" 5
wait_for "etickets.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf "$API/api/e-tickets/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the e-ticket row"
[ "$(order_field "$IDP" ticketsReleasedAt)" != None ] || fail "order should record the release"
say "rejected, dead-lettered (+1 in etickets.work.dlq), remaining back to 5"

echo "5. refund races the final mark: the refund wins, the issued e-ticket stays"
open_tier "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "BUYER-$RUN-R$attempt" 2)"
  # The e-ticket exists before the order is marked Sold (the final mark is delayed in compose).
  wait_for "e-ticket to exist" curl -sf "$API/api/e-tickets/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (order already Sold); the refund still has to hold; retrying"
  curl -sf -X POST "$API/api/orders/$IDR/refund" >/dev/null || true
  wait_for "order to be Refunded" status_is "$IDR" Refunded
done
[ "$WON" = 1 ] || fail "never caught the order between e-ticket issued and final mark"
curl -sf -X POST "$API/api/orders/$IDR/refund" >/dev/null || fail "refund was refused"
wait_for "order to be Refunded" status_is "$IDR" Refunded
sleep 3 # longer than the final-mark delay: the late mark must not undo the refund
status_is "$IDR" Refunded || fail "the final mark overwrote the refund"
[ "$(curl -sf "$API/api/e-tickets/$IDR" | jget "['status']")" = Issued ] || fail "the issued e-ticket was rolled back"
[ "$(order_field "$IDR" ticketNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "tickets to come back" remaining_is "R-$RUN" 6
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/orders/$IDR/refund")"
[ "$code" = 409 ] || fail "second refund should be 409, got $code"
say "order Refunded, e-ticket still Issued, remaining 6, second refund 409"

echo "tickets smoke passed"
