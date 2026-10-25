#!/usr/bin/env bash
# Verifies the database-level booking invariants against a REAL PostgreSQL.
# Usage: PGHOST=localhost PGUSER=veskaflow PGPASSWORD=veskaflow PGDATABASE=veskaflow_test tests/db/double_booking.sh
# WARNING: wipes the public schema of $PGDATABASE and re-applies migrations. Use a disposable database.
set -uo pipefail
cd "$(dirname "$0")/../.."
P="psql -X -q -t -A -v ON_ERROR_STOP=1"
fail=0
inserted() { if echo "$1" | grep -q ERROR; then echo 0; else echo 1; fi; }
check() { if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected '$3', got '$2')"; fail=1; fi; }

$P -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;" >/dev/null
for f in src/VeskaFlow.Web/Data/Migrations/*.sql; do $P -f "$f" >/dev/null || { echo "migration $f failed"; exit 1; }; done

B1=$($P -c "INSERT INTO business (slug,name) VALUES ('shop-one','Shop One') RETURNING id" | head -1)
B2=$($P -c "INSERT INTO business (slug,name) VALUES ('shop-two','Shop Two') RETURNING id" | head -1)
S1=$($P -c "INSERT INTO staff (business_id,name) VALUES ('$B1','Ben') RETURNING id" | head -1)
S1b=$($P -c "INSERT INTO staff (business_id,name) VALUES ('$B1','Carl') RETURNING id" | head -1)
S2=$($P -c "INSERT INTO staff (business_id,name) VALUES ('$B2','Other') RETURNING id" | head -1)
SV1=$($P -c "INSERT INTO service (business_id,name,duration_minutes,price) VALUES ('$B1','Haircut',30,150) RETURNING id" | head -1)
C1=$($P -c "INSERT INTO customer (business_id,name,phone) VALUES ('$B1','Juan','0917') RETURNING id" | head -1)

# args: staff start end occupiedUntil status token
ins() {
  $P -c "INSERT INTO booking (business_id,customer_id,staff_id,service_id,starts_at_utc,ends_at_utc,occupied_until_utc,
         location_time_zone,status,price_snapshot,service_name_snapshot,manage_token)
         VALUES ('$B1','$C1','$1','$SV1','$2','$3','$4','Asia/Manila','$5',150,'Haircut','$6')" 2>&1
}
T=2026-10-20

echo "Race: 40 simultaneous bookings of the same slot"
rm -f /tmp/race_*.out
for i in $(seq 1 40); do ( ins "$S1" "$T 02:00+00" "$T 02:30+00" "$T 02:30+00" CONFIRMED "race-$i" >/tmp/race_$i.out 2>&1; echo $? >/tmp/race_$i.rc ) & done
wait
ok=$(cat /tmp/race_*.rc | grep -c '^0$')
excl=$(grep -l 'no_double_booking' /tmp/race_*.out 2>/dev/null | wc -l | tr -d ' ')
check "exactly one booking wins" "$ok" "1"
check "the other 39 are rejected by the exclusion constraint" "$excl" "39"
check "only one active row exists for the slot" "$($P -c "SELECT count(*) FROM booking WHERE staff_id='$S1'")" "1"

echo "Overlaps and adjacency"
r=$(ins "$S1" "$T 02:15+00" "$T 02:45+00" "$T 02:45+00" CONFIRMED a1); check "partial overlap rejected" "$(echo "$r" | grep -c no_double_booking)" "1"
r=$(ins "$S1" "$T 01:45+00" "$T 02:15+00" "$T 02:15+00" CONFIRMED a2); check "overlap from the left rejected" "$(echo "$r" | grep -c no_double_booking)" "1"
r=$(ins "$S1" "$T 02:30+00" "$T 03:00+00" "$T 03:00+00" CONFIRMED a3); check "back-to-back slot allowed" "$(inserted "$r")" "1"
r=$(ins "$S1" "$T 01:30+00" "$T 02:00+00" "$T 02:00+00" CONFIRMED a4); check "slot ending exactly when next starts allowed" "$(inserted "$r")" "1"
r=$(ins "$S1b" "$T 02:00+00" "$T 02:30+00" "$T 02:30+00" CONFIRMED a5); check "same time with a different staff member allowed" "$(inserted "$r")" "1"

echo "Buffers"
r=$(ins "$S1" "$T 05:00+00" "$T 05:30+00" "$T 05:45+00" CONFIRMED b1); check "booking with 15 min buffer inserted" "$(inserted "$r")" "1"
r=$(ins "$S1" "$T 05:30+00" "$T 06:00+00" "$T 06:00+00" CONFIRMED b2); check "slot inside the buffer rejected" "$(echo "$r" | grep -c no_double_booking)" "1"
r=$(ins "$S1" "$T 05:45+00" "$T 06:15+00" "$T 06:15+00" CONFIRMED b3); check "slot right after the buffer allowed" "$(inserted "$r")" "1"

echo "Status lifecycle frees or holds the slot"
$P -c "UPDATE booking SET status='CANCELLED', cancelled_at_utc=now() WHERE manage_token='b1'" >/dev/null
r=$(ins "$S1" "$T 05:00+00" "$T 05:30+00" "$T 05:30+00" CONFIRMED b4); check "cancelling frees the slot immediately" "$(inserted "$r")" "1"
r=$(ins "$S1" "$T 07:00+00" "$T 07:30+00" "$T 07:30+00" HELD h1); check "HELD booking inserted" "$(inserted "$r")" "1"
r=$(ins "$S1" "$T 07:00+00" "$T 07:30+00" "$T 07:30+00" CONFIRMED h2); check "HELD blocks the slot" "$(echo "$r" | grep -c no_double_booking)" "1"
$P -c "UPDATE booking SET status='NO_SHOW' WHERE manage_token='b4'" >/dev/null
r=$(ins "$S1" "$T 05:00+00" "$T 05:30+00" "$T 05:30+00" CONFIRMED b5); check "NO_SHOW does not block the slot" "$(inserted "$r")" "1"
r=$(ins "$S1" "$T 09:00+00" "$T 09:30+00" "$T 09:30+00" BOGUS x1); check "invalid status rejected by CHECK" "$(echo "$r" | grep -c booking_status)" "1"

echo "Integrity"
r=$(ins "$S2" "$T 10:00+00" "$T 10:30+00" "$T 10:30+00" CONFIRMED t1); check "cross-tenant staff reference rejected by composite FK" "$(echo "$r" | grep -c 'violates foreign key')" "1"
r=$(ins "$S1" "$T 10:00+00" "$T 10:30+00" "$T 10:30+00" CONFIRMED b5); check "duplicate manage_token rejected" "$(echo "$r" | grep -c ux_booking_manage_token)" "1"
r=$($P -c "INSERT INTO business (slug,name) VALUES ('Bad Slug','x')" 2>&1); check "invalid slug rejected" "$(echo "$r" | grep -c business_slug_format)" "1"
r=$($P -c "UPDATE booking SET ends_at_utc = starts_at_utc WHERE manage_token='b5'" 2>&1); check "zero-length booking rejected" "$(echo "$r" | grep -c booking_range)" "1"

echo; [ $fail -eq 0 ] && echo "All database invariants hold." || echo "DATABASE CHECKS FAILED."
exit $fail
