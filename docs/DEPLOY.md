# Deploying VeskaFlow on free tiers

Target: Render (the app, built from the `Dockerfile`), Neon (PostgreSQL 16), Cloudflare (DNS in front). Everything here is free; limits and costs are in `BUSINESS_RULES.md` section 17 and in the cost notes at the end.

An account on each service is needed and only the owner can create them. Nothing below puts a secret in the repository.

## 0. Safety gate before real customer data (BUSINESS_RULES 17.3)

The no-double-booking guarantee needs the `btree_gist` extension. Prove it on Neon before going live:

1. In Neon, create a project on **PostgreSQL 16**. Create a second database named `veskaflow_test` in it (or a separate branch).
2. In the SQL editor of the **test** database run `CREATE EXTENSION IF NOT EXISTS btree_gist;`. It must succeed.
3. From a machine with `psql`, run the invariant script against the test database only (it wipes the public schema):
   ```bash
   PGHOST=<neon host> PGUSER=<user> PGPASSWORD=<password> PGDATABASE=veskaflow_test PGSSLMODE=require tests/db/double_booking.sh
   ```
   It must print "All database invariants hold." If it does not, stop and use the fallback in 17.3.

## 1. Database (Neon)

1. Use the main database of the project for the app (for example `neondb`).
2. Copy the **direct** connection details, not the one with `-pooler` in the host (the app takes transaction advisory locks; pooled behaviour has not been checked).
3. Build the connection string in this form:
   ```
   Host=<neon host>;Database=<db>;Username=<user>;Password=<password>;SSL Mode=Require
   ```
4. The app creates all tables itself on first start (`Database:MigrateOnStartup`, default on). A second start applies nothing.

## 2. App (Render)

Option A, Blueprint (fewest clicks): in Render choose **New, Blueprint**, pick this GitHub repository (grant Render access to it, it is private), and it reads `render.yaml`. When asked for `ConnectionStrings__Default`, paste the string from step 1.

Option B, manually: **New, Web Service, Docker**, this repository, plan **Free**, health check path `/healthz`, and these environment variables:

| Variable | Value |
|---|---|
| `ConnectionStrings__Default` | the Neon string (secret) |
| `Proxy__TrustForwardedHeaders` | `true` (so rate limits see the visitor's IP, not Render's) |
| `Trial__Days` | `30` for the shared trial. `0` for a client's own deployment |
| `Database__MigrateOnStartup` | `true` |

Who may use the system (all optional, set in the same place; never commit secrets):

| Variable | What it does |
|---|---|
| `Platform__AdminEmails` | Your own email (comma separated for more than one). Sign in with it and open `/Platform`: the list of shops with Switch on / Switch off, Confirm email now and Send confirmation link. Everyone else gets "not found". Needs an account with that email, already confirmed |
| `Signup__RequireEmailConfirmation` | `true` (default): a new owner cannot sign in until they open the link in the email. `false` for a client's own deployment |
| `Signup__RequireApproval` | `true`: a new shop stays switched off until you switch it on in `/Platform`. Default `false` |
| `Email__BrevoApiKey`, `Email__From` and `Email__FromName` | Sends the confirmation email through Brevo's HTTP API (free, about 300 emails a day, no domain setup, and Render's free plan blocks SMTP). It is the same Brevo account the veskacode.com contact form uses: copy `BREVO_API_KEY` from the Cloudflare Pages project into `Email__BrevoApiKey`, `VERIFY_FROM_EMAIL` (a sender validated in Brevo) into `Email__From`, and `VERIFY_FROM_NAME` into `Email__FromName`. Without a key and sender nothing is emailed: use `/Platform` to confirm an owner by hand or copy a link to send yourself |
| `Email__ResendApiKey` and `Email__From` | Alternative to Brevo (needs a domain verified in Resend). Used only when Brevo is not configured |

Existing accounts were marked confirmed by migration 009, so they keep working. To keep a free uptime monitor from letting the app sleep, point it at `/ping` (it never touches the database); `/healthz` does touch it.

After the first deploy, open `https://<name>.onrender.com/healthz`. It returns `{"status":"ok","version":...}` when the database is reachable and `503` when it is not.

## 3. Domain (Cloudflare)

1. Render: service, **Settings, Custom Domains**, add `book.<your domain>`.
2. Cloudflare DNS: add a `CNAME` record named `book` pointing to the `.onrender.com` address Render shows. Leave it **DNS only** (grey cloud) at first. With the orange cloud on, the proxy chain changes the forwarded IP header and the per-IP rate limit has not been checked behind it.
3. Do not touch the records for the root domain or existing email.
4. Wait for Render to issue the certificate, then open `https://book.<your domain>/Register`.

## 4. Check after deploy

- `/healthz` returns ok.
- Register a test clinic, see the trial banner, add a service price, open `/b/<slug>` on a phone and book a time.
- The booking shows in `/Admin` and the time disappears from the public list.
- Delete the test shop afterwards (operator task, see below).

## 5. Backups (before real data)

The free database keeps a very short history. Take a regular dump and keep it outside Neon:

```bash
pg_dump "postgresql://<user>:<password>@<host>/<db>?sslmode=require" -Fc -f veskaflow-$(date +%F).dump
```

Restore it once into an empty database to prove it works (`pg_restore -d <empty db> <file>`). A scheduled job can run this daily and upload the file to storage such as Cloudflare R2 (free allowance, verify current limits). That job is not built yet.

## 6. Things to tell the client (free hosting)

- No uptime guarantee. After about 15 minutes without visits the app sleeps and the next visit takes around a minute.
- Limited database history, so backups are the owner's job.
- The trial closes online booking after 30 days. Staff can still use the admin.

## 7. Deleting a tenant (by hand)

There is no delete button. After confirming with the business, delete in this order inside one transaction, using the business id: `audit_log`, `booking`, `customer`, `blackout`, `staff_service`, `working_hour`, `staff`, `service`, `admin_user`, `business`. Take a dump first.

## 8. Moving to paid hosting

Triggers are in `BUSINESS_RULES.md` 17.1 (first paying client, database near its limits, daily email cap). A client who hosts for themselves uses the same `Dockerfile` and variables with `Trial__Days=0`.

## What was and was not verified here

Verified on Neon (PostgreSQL 18.6, Singapore): `btree_gist` works, `tests/db/double_booking.sh` passes, and the Web suite passes 91 of 91 (step 0 is done for that project).
Verified: the project publishes in Release, runs in Production mode on the port given in `PORT`, serves `/healthz` and a booking page, with forwarded headers on.
Not verified: the Docker image build (no Docker on the development machine), a real Render deploy, the custom domain, and behaviour behind the Cloudflare proxy.

## Lessons from the first deploy attempt

- **Connection string format.** Neon's "Connection string" box shows a `postgresql://...` URL. The app needs the key/value form: `Host=...;Database=...;Username=...;Password=...;SSL Mode=Require`. A URL stops startup with a clear message (the value is no longer echoed, so it cannot leak into logs).
- **Region.** `render.yaml` sets `region: singapore` to match the Neon project. A Render service's region cannot be changed later; to move one, delete the service and sync the Blueprint again.
- **Auto Sync.** A Blueprint created from this repository syncs automatically on every push to `main`. Switch Auto Sync to No in the Blueprint settings if a change to `render.yaml` should wait for a manual sync.
- **Never paste a log or screenshot that shows a connection string.** If one leaks, reset the database password in Neon (Connect, Reset password) and update the Render environment variable.

## Extending, lifting or ending a trial (operator)

A trial is the column `trial_ends_at_utc` on the `business` row. When it has passed, the owner is locked out of the back office and the public page is closed; nothing is deleted.

```sql
-- extend by 14 days from now
UPDATE business SET trial_ends_at_utc = now() + interval '14 days' WHERE slug = '<slug>';
-- lift the trial (a paying client): no limit, no lock
UPDATE business SET trial_ends_at_utc = NULL WHERE slug = '<slug>';
```

The first deploy of a change that sets the trial needs a restart-free redeploy only; the lock is checked on every request.

## Keeping the free Render service awake

Render's free web service sleeps after 15 minutes without traffic, and the first visit then waits about a minute. Two free ways to avoid that, both calling `/ping` (no database, so Neon can still sleep):

1. **GitHub Actions** (already in the repo): `.github/workflows/keepalive.yml` calls `https://flow.veskacode.com/ping` every 14 minutes. GitHub may delay a run by a few minutes and pauses scheduled runs after 60 days without repository activity. If the repository is private, the free monthly minutes (2,000) may run out; then use option 2.
2. **An outside monitor** (UptimeRobot or cron-job.org, free account): add an HTTP monitor for `https://flow.veskacode.com/ping` every 5 minutes. It also emails you when the site is down.

## Sign-up bot check (Cloudflare Turnstile, free)

Sign-up and forgot password can ask visitors to pass a quick "are you a person" check. It is off until both keys are set:

1. In Cloudflare, **Turnstile**, **Add widget**. Hostname: `flow.veskacode.com`. Mode: Managed. Copy the **Site key** and the **Secret key**.
2. In Render, Environment, add `Turnstile__SiteKey` (the site key) and `Turnstile__SecretKey` (the secret key), then deploy. Keep the secret key out of chats, commits and screenshots.
3. Open the sign-up page: the check box appears above the button. Try a sign-up on a test email.

To switch it off, remove the two variables. With the keys set, a failed or unreachable check always refuses the request (it never lets a bot through); if Cloudflare is down, sign-ups wait. The page's content policy then also allows `challenges.cloudflare.com`, and only when the keys are set.
