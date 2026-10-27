# Backups

The database (Neon free) holds every shop, booking, customer and record. Neon's free plan only keeps a short history, so
this repository can make its own copy every night.

## What happens

`.github/workflows/backup.yml` runs every night at 03:00 Manila time (and on demand from the Actions tab). It runs
`scripts/backup.sh`, which:

1. dumps the whole database with `pg_dump` (custom format),
2. **encrypts** it with AES-256 using your passphrase (the file holds customer names and phone numbers, and workflow
   artifacts can be read by anyone with access to the repository),
3. reads it back to check it opens, and fails the run if it does not,
4. keeps it as the workflow artifact `veskaflow-backup` for 14 days.

If a run fails, GitHub emails you. Nothing in the backup is ever written unencrypted.

## One-time setup (about 5 minutes)

1. In Neon, open the project, then **Connect**, turn **off** "Connection pooling", and copy the connection string. It must
   use the direct host (no `-pooler` in the address). Treat it like a password.
2. Choose a long passphrase (for example four random words and a number) and **save it in your password manager**. Without
   it, a backup cannot be opened and nobody can recover it for you.
3. In GitHub: the repository, **Settings**, **Secrets and variables**, **Actions**, **New repository secret**. Add two:
   - `BACKUP_DATABASE_URL`: the Neon connection string from step 1.
   - `BACKUP_PASSPHRASE`: the passphrase from step 2.
4. **Actions** tab, **backup**, **Run workflow**. When it is green, open the run: the artifact `veskaflow-backup` is there.

Never paste the connection string or the passphrase in a chat, a commit or an issue.

## Restore (try it once, so you know it works)

You need PostgreSQL client tools (`pg_restore`, `createdb`) and `gpg`. Download the artifact from the run page and unzip it.

```bash
# 1. a new, empty database (use a scratch one first; restoring replaces nothing by itself)
createdb -h <host> -U <user> veskaflow_restored

# 2. decrypt and restore
gpg --decrypt veskaflow-YYYYMMDD-HHMM.dump.gpg | pg_restore --no-owner --dbname="postgresql://<user>:<password>@<host>/veskaflow_restored"
```

`gpg` asks for the passphrase. Check the data (for example `select count(*) from booking;`). To go live from a restored
database, point `ConnectionStrings__Default` in Render at it and redeploy; the app applies any newer migrations on start.

## Limits

- It is a nightly copy: up to a day of bookings can be lost in the worst case. Neon's own short history can cover recent
  hours; use Neon's restore feature for that.
- 14 days of copies. Download one now and then to your own computer or drive for a longer history.
- If the repository is private, artifact storage counts against the free GitHub quota; the file is small (a few hundred KB at first).
