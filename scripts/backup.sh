#!/usr/bin/env bash
# Encrypted backup of the whole database.
#   DATABASE_URL       connection string of the database to back up (use Neon's direct host, not the "-pooler" one)
#   BACKUP_PASSPHRASE  passphrase the file is encrypted with (keep it somewhere safe: without it the backup cannot be opened)
#   $1                 folder to write to (default: backups)
# The file holds customer names and phone numbers, so it is only ever written encrypted (AES-256). It is checked after
# writing: decrypted and read back with pg_restore --list.
set -euo pipefail

: "${DATABASE_URL:?Set DATABASE_URL to the database connection string}"
: "${BACKUP_PASSPHRASE:?Set BACKUP_PASSPHRASE to the passphrase for the encrypted file}"

out_dir="${1:-backups}"
mkdir -p "$out_dir"
stamp="$(date -u +%Y%m%d-%H%M)"
file="$out_dir/veskaflow-$stamp.dump.gpg"

pg_dump --format=custom --no-owner --no-privileges --dbname="$DATABASE_URL" \
  | gpg --batch --yes --quiet --pinentry-mode loopback --symmetric --cipher-algo AES256 --passphrase-fd 3 --output "$file" 3<<<"$BACKUP_PASSPHRASE"

# Read it back: a backup that cannot be opened is not a backup.
tables="$(gpg --batch --quiet --pinentry-mode loopback --decrypt --passphrase-fd 3 "$file" 3<<<"$BACKUP_PASSPHRASE" 2>/dev/null | pg_restore --list | grep -c 'TABLE DATA' || true)"
if [ "${tables:-0}" -lt 1 ]; then
  echo "Backup check failed: no table data found in $file" >&2
  rm -f "$file"
  exit 1
fi
echo "Backup written: $file ($(wc -c < "$file") bytes, $tables tables with data)"
