# CLAUDE.md

Guidance for Claude (and any developer) working in this repository. Read this first, then `docs/BUSINESS_RULES.md`.

## What this is

VeskaFlow: multi-tenant booking SaaS for barbershops and salons. ASP.NET Core 8 (Razor Pages), PostgreSQL 16, EF Core with Npgsql. A Flutter mobile app and a JSON API (`/api/v1`) come later. The goal is **free first**: no paid service until paying clients exist (see section 17 of the rules).

## Source of truth

`docs/BUSINESS_RULES.md` decides behavior. If code and the document disagree, stop and raise it. Change rules and code in the same commit. Section 4 (invariants) is non-negotiable.

## Layout

```
src/VeskaFlow.Core/        Pure C#, no NuGet packages. Availability, time zones, state machine, policies, password hashing, slugs.
src/VeskaFlow.Web/         ASP.NET Core app. Data/ (entities, DbContext, SQL migrator, Migrations/*.sql), later Services/ and Pages/.
tests/VeskaFlow.Core.SelfCheck/   Dependency-free test runner for Core.
tests/db/double_booking.sh  Database invariant tests against a real PostgreSQL.
docs/                       BUSINESS_RULES.md (spec), BUILD_PLAN.md (what to build next, with acceptance criteria).
```

## Commands

```bash
dotnet run --project tests/VeskaFlow.Core.SelfCheck          # must print "All N checks passed."
dotnet build src/VeskaFlow.Web                               # needs NuGet access
# Integration tests need a disposable database whose name ends in _test (its schema is wiped):
ConnectionStrings__Test="Host=localhost;Database=veskaflow_test;Username=postgres;Password=..." dotnet test tests/VeskaFlow.Web.Tests
# Disposable database only. The script wipes its public schema.
PGHOST=localhost PGUSER=... PGPASSWORD=... PGDATABASE=veskaflow_test tests/db/double_booking.sh
```

## Rules for code

- Business logic never lives in pages or controllers. Pages call services; services call `VeskaFlow.Core`.
- `VeskaFlow.Core` stays free of NuGet dependencies and of EF/ASP.NET types.
- Availability is derived by `AvailabilityCalculator`, never stored, and is re-checked on the server at confirmation.
- Double-booking protection is the database exclusion constraint. Translate PostgreSQL `23P01` into a normal "slot taken" result, never a 500.
- Tenant isolation: every back-office query is filtered by the `BusinessId` from the signed-in user's claims, never from the request. Unknown id, wrong tenant and missing token all return not found.
- Times are UTC in the database and in code. The business IANA time zone is used only to interpret local times.
- Database changes: new numbered file in `src/VeskaFlow.Web/Data/Migrations/` (embedded resource). Never edit an applied migration. Additive only. No `DROP`, `TRUNCATE` or destructive `ALTER` without an explicit, reviewed plan.
- EF column names are snake_case of the property names (see `AppDbContext`). Keep them matching the SQL.
- No secrets in git. Connection string comes from `ConnectionStrings__Default`.
- All user-facing text is English and kept in one place so Tagalog can be added later.
- Font Awesome for icons. No emoji as UI icons.

## Honesty about verification

Mark anything not compiled or not run as unverified, in the commit message and in `docs/BUILD_PLAN.md`. Never claim "done" or "production-ready" with failing or unrun tests.

## Current state

Verified: Core (65 self-checks), the database schema (20 checks on PostgreSQL 16), the EF model against the schema (ModelCheck) and the Web test suite (209 tests on PostgreSQL 16.11). Checked in a local browser (desktop and phone size): Reports, Team, Settings logo upload to booking page, Customers. Not verified: the rest of the newer pages on a real phone, and email delivery through Resend (see the table in `docs/BUILD_PLAN.md`).
Compiled and checked against PostgreSQL 16 (Step 0 done): `src/VeskaFlow.Web/Data/*`, `AppInfo.cs`; run `tests/VeskaFlow.Web.ModelCheck` (needs `ConnectionStrings__Default` pointing at a disposable database) to confirm the EF model matches the schema. `Services/BookingService.cs` (Step 2) is covered by `tests/VeskaFlow.Web.Tests` against real PostgreSQL.

## Working from two places (VS Code and mobile)

GitHub is the single source of truth.

- Start every session with `git pull --rebase origin main`. Commit small, push often, never leave work only on one device.
- The full .NET build and the web app are developed and verified on the developer's PC (VS Code), where NuGet works.
- Mobile or cloud sessions can push directly to `main` for rules, docs, SQL migrations and Core logic that the self-check can verify. They must not push large Web changes they could not compile; use a branch such as `mobile/<topic>` and say so in the commit message.
- If a push is rejected because the remote moved, pull with rebase and retry. Never force-push `main`.

## Commit messages

Short imperative summary, then what and why. Mention what was verified and what was not.
