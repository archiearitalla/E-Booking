# E-Booking
Online Booking System

**BarberBook**: multi-tenant booking SaaS for barbershops and salons. ASP.NET Core 8, PostgreSQL 16, Flutter mobile client planned.

Read [`docs/BUSINESS_RULES.md`](docs/BUSINESS_RULES.md) first. It is the source of truth for behavior, invariants, API plan, free-tier providers and open decisions.

## Where to start

- Spec: [`docs/BUSINESS_RULES.md`](docs/BUSINESS_RULES.md)
- What to build next, with acceptance criteria: [`docs/BUILD_PLAN.md`](docs/BUILD_PLAN.md)
- Working conventions and the VS Code / mobile workflow: [`CLAUDE.md`](CLAUDE.md)

## Status

| Part | State |
|---|---|
| `src/BarberBook.Core`: availability, DST-safe time, state machine, cancellation policy, password hashing | Done. 55 self-checks pass |
| `src/BarberBook.Web/Data/Migrations/001_init.sql`: schema with the `no_double_booking` exclusion constraint | Done. 20 database checks pass on PostgreSQL 16 |
| `src/BarberBook.Web`: entities, `AppDbContext`, SQL migrator | Written, **not compiled yet** (NuGet was unreachable where it was written) |
| `src/BarberBook.Web`: `Program.cs`, services, Razor pages, admin | Not written yet |
| JSON API `/api/v1`, Flutter app, notifications, payments | Planned |

`BarberBook.sln` currently contains only the projects that build offline (Core and the self-check runner). Add `src/BarberBook.Web` to the solution once it has an entry point.

## Run the checks

```bash
# Core logic (no NuGet packages needed)
dotnet run --project tests/BarberBook.Core.SelfCheck

# Database invariants: needs a DISPOSABLE PostgreSQL database (the script wipes its public schema)
PGHOST=localhost PGUSER=postgres PGPASSWORD=... PGDATABASE=barberbook_test tests/db/double_booking.sh
```

The database script needs `psql`, and the server must have the `btree_gist` extension available.

## Rules of the repository

- No secrets in git. Connection strings come from environment variables (`ConnectionStrings__Default`).
- Database changes are additive numbered migrations in `src/BarberBook.Web/Data/Migrations/`. No `DROP`, `TRUNCATE` or destructive `ALTER` without a reviewed plan.
- Business rules change in `docs/BUSINESS_RULES.md` in the same commit as the code.
