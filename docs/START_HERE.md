# START HERE: building VeskaFlow on your PC

One file to open Claude Code (or any developer) with. It tells you what to install, what to paste, and in what order to build. The details live in the other docs; this is the entry point.

## 1. Install once

| Tool | Why |
|---|---|
| .NET 8 SDK | Build and run the app |
| PostgreSQL 16 (or Docker) | The app and the database tests need a real Postgres |
| Git, VS Code, Claude Code | Source control and the coding assistant |

Postgres with Docker (the official image includes `btree_gist`):

```bash
docker run -d --name veskaflow-pg -e POSTGRES_PASSWORD=veskaflow -p 5432:5432 postgres:16
docker exec -it veskaflow-pg psql -U postgres -c "CREATE DATABASE veskaflow;"
docker exec -it veskaflow-pg psql -U postgres -c "CREATE DATABASE veskaflow_test;"
```

If you install Postgres directly, make sure the contrib package is included so `CREATE EXTENSION btree_gist` works.

Connection strings (environment variables, never committed):

```bash
# bash
export ConnectionStrings__Default="Host=localhost;Database=veskaflow;Username=postgres;Password=veskaflow"
# PowerShell
$env:ConnectionStrings__Default = "Host=localhost;Database=veskaflow;Username=postgres;Password=veskaflow"
```

## 2. Get the code

```bash
git clone https://github.com/archiearitalla/E-Booking
cd E-Booking
git pull --rebase origin main     # do this at the start of every session
```

Quick sanity check that works with no NuGet access:

```bash
dotnet run --project tests/VeskaFlow.Core.SelfCheck     # expect: All 55 checks passed.
```

## 3. Paste this into Claude Code

```text
Read CLAUDE.md, docs/BUSINESS_RULES.md and docs/BUILD_PLAN.md in full before changing anything.

We are building the VeskaFlow MVP (ASP.NET Core 8 Razor Pages, PostgreSQL 16, EF Core) from the
existing code. Work through docs/BUILD_PLAN.md in order, starting at Step 0. For each step:

1. Implement only that step. Follow the rules in CLAUDE.md and the behavior in BUSINESS_RULES.md.
2. Run the checks listed under that step's "Acceptance" and show me the real output.
3. Do not start the next step until the acceptance criteria pass. If something cannot be
   verified, say so plainly and mark it unverified instead of claiming it is done.
4. Commit after each step with a clear message that states what was verified and what was not.
   Pull with rebase before pushing. Never force-push main.

Hard constraints:
- The double-booking guarantee is the database exclusion constraint. Never replace it with
  application-only checks.
- No business logic in pages. Availability is derived by VeskaFlow.Core, never stored.
- Every back-office query is scoped by the BusinessId from the signed-in user's claims.
- Database changes are additive numbered migrations only. No DROP, TRUNCATE or destructive ALTER.
- Everything is free-tier first. Do not add a paid service or package that needs payment.
- If the code disagrees with docs/BUSINESS_RULES.md, stop and ask me.

Start now with Step 0: restore, add the web project to the solution, make it build, fix compile
errors in src/VeskaFlow.Web/Data, and run all three checks (self-check, database script, build).
```

## 4. Decisions already made (do not reopen without asking)

| Topic | Decision |
|---|---|
| Stack | ASP.NET Core 8 Razor Pages, PostgreSQL 16, EF Core (Npgsql), Flutter later |
| Cost | Free first; pay only when paying clients exist |
| Database | Neon Postgres Free, PostgreSQL 16 (Supabase Free is the fallback). Gate: `btree_gist` and the database script must pass on a separate Neon branch before go-live |
| Hosting | Render free web service (Docker) or Cloud Run; Cloudflare only as DNS/CDN in front |
| Language | English only for the MVP |
| Cancel cutoff | 2 hours, per-business setting |
| Walk-ins | Skip lead time; still respect hours, blackouts and overlaps |
| Customer key | Phone number (email optional) |
| Reminders | No SMS gateway. Prefilled message links (`sms:` and Messenger copy-and-open); optional free email later |
| Mobile auth | Short-lived JWT plus refresh token (later, with the API) |
| Plans | All features free for pilot shops; FREE plan keeps the 1-staff limit |
| Retention | 24 months after last booking, then anonymize |

## 5. What is verified and what is not

- Verified: `VeskaFlow.Core` (55 self-checks) and the SQL schema (20 checks on PostgreSQL 16).
- Not compiled yet: `src/VeskaFlow.Web/Data/*` and `AppInfo.cs`. Step 0 fixes that.
- Not written yet: `Program.cs`, services, pages, admin, API, Flutter app.

## 6. When you switch between PC and mobile

GitHub is the single source of truth. Pull before you start, push before you stop. Large Web changes made without a compiler go on a `mobile/<topic>` branch, not on `main`. See `CLAUDE.md`.
