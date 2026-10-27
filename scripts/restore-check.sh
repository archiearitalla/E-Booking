#!/usr/bin/env bash
# Proves a backup can be restored: decrypts the newest file in a folder, restores it into an EMPTY scratch PostgreSQL
# server and counts rows. Never touches the live database.
#   RESTORE_URL        connection string of an empty scratch database (for example a throwaway container)
#   BACKUP_PASSPHRASE  the passphrase the backup was encrypted with
#   $1                 folder holding the backup file (default: backups)
# Prints only counts, never data.
set -euo pipefail

: "${RESTORE_URL:?Set RESTORE_URL to the scratch database connection string}"
: "${BACKUP_PASSPHRASE:?Set BACKUP_PASSPHRASE to the passphrase for the encrypted file}"

dir="${1:-backups}"
file="$(ls -1t "$dir"/*.dump.gpg 2>/dev/null | head -n 1 || true)"
[ -n "$file" ] || { echo "No backup file found in $dir" >&2; exit 1; }

gpg --batch --quiet --pinentry-mode loopback --decrypt --passphrase-fd 3 "$file" 3<<<"$BACKUP_PASSPHRASE" \
  | pg_restore --no-owner --no-privileges --exit-on-error --dbname="$RESTORE_URL"

counts="$(psql -tA -F ' ' -c "select 'shops', count(*) from business union all select 'bookings', count(*) from booking union all select 'customers', count(*) from customer union all select 'migrations', count(*) from schema_migrations" "$RESTORE_URL")"
echo "Restore check passed for $file:"
echo "$counts"

shops="$(echo "$counts" | awk '$1=="shops"{print $2}')"
if [ "${shops:-0}" -lt 1 ]; then
  echo "Restore check failed: the restored database has no shops" >&2
  exit 1
fi
