# Build plan

Ordered steps to finish the MVP. Each step lists the files to touch and the acceptance criteria that prove it works. Do not move on until the current step's criteria pass. Behavior comes from `BUSINESS_RULES.md`; section numbers below refer to it.

Legend: ✅ done and verified · 🟡 written, not compiled · ⬜ not started

## Step 0: Make the existing code build ✅

Verified on Windows, .NET SDK 8.0.426 and 10.0.401 installed, PostgreSQL 16.15: build 0 warnings 0 errors; self-check 55/55; `double_booking.sh` 20/20; `tests/BarberBook.Web.ModelCheck` confirms the EF model matches the migrated schema and a booking round-trips through EF. The Data files needed no fixes. A placeholder `Program.cs` (with `/healthz`) was added so the project builds; Step 1 replaces it.

Files: `BarberBook.sln`, `src/BarberBook.Web/*`

1. `dotnet restore` (needs NuGet access).
2. Add the web project: `dotnet sln add src/BarberBook.Web`. It has no entry point yet, so also create a minimal `Program.cs` in Step 1 before building it.
3. Fix any compile errors in `Data/Entities.cs`, `Data/AppDbContext.cs`, `Data/SqlMigrator.cs`, `AppInfo.cs`.

Acceptance:
- `dotnet build BarberBook.sln` succeeds with no warnings treated as errors.
- `dotnet run --project tests/BarberBook.Core.SelfCheck` prints "All 55 checks passed."
- `tests/db/double_booking.sh` prints "All database invariants hold."
- `AppDbContext` model matches the SQL: write a small check that opens the context and runs `db.Bookings.Take(1)`, `db.Staff...` etc. against a migrated database without a column-name error.

## Step 1: Program.cs and startup ⬜

Files: `src/BarberBook.Web/Program.cs`, `appsettings.json`, `appsettings.Development.json`, `Pages/Error.cshtml`.

- DI: `AddDbContextFactory<AppDbContext>` plus a scoped context from the factory, `TimeProvider.System`.
- Cookie authentication (`HttpOnly`, `SameSite=Lax`), authorization, folder policy on `/Admin`.
- Rate limiter policies `auth` and `booking`, partitioned by client IP.
- Security headers (CSP without inline scripts, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`), HSTS outside development.
- Run `SqlMigrator.ApplyAsync` on startup (configurable).
- `GET /healthz` returns status and `AppInfo.Version`.

Acceptance:
- App starts against an empty database and creates all tables; a second start applies nothing.
- `/healthz` returns 200 with the version; with the database stopped it returns 503.
- Response headers contain the security headers.

## Step 2: BookingService ⬜

Files: `Services/BookingService.cs`, `Services/BookingResults.cs`.

Implements sections 6, 7 and 8: `GetFreeStartsAsync`, `CreateAsync` (any-available assignment, idempotency, retry on `23P01`), `TransitionAsync` (state machine, policy, audit row, `xmin` concurrency).

Acceptance (integration tests against a real PostgreSQL):
- 50 parallel `CreateAsync` for the same slot: exactly one success, the rest `SlotTaken`, no exception escapes.
- "Any available" with two staff: two parallel requests for one time both succeed on different staff; a third gets `SlotTaken`.
- Same idempotency key twice returns the same booking and creates one row.
- Lead time, horizon, blackout, buffer and working hours are enforced on the server even when the posted start time came from a stale page.
- Customer cancel after the cutoff is refused; staff cancel succeeds; every change writes an audit row.
- A booking id from another business returns not found.

## Step 3: Onboarding and sign-in ⬜

Files: `Services/OnboardingService.cs`, `Services/AuthService.cs`, `Pages/Register`, `Pages/Login`, `Pages/Logout`.

Register creates business, owner, one staff, one service and Mon-Sat 09:00-18:00 hours in one transaction. FREE plan allows 1 staff (enforce in the staff page too).

Acceptance:
- Registering a taken slug or email shows a friendly error, not a 500.
- Wrong password and unknown email produce the same message and similar timing.
- After sign-in, `User` has `business_id`, role and display name claims. Sign-out clears the cookie.
- More than the allowed attempts per minute returns 429.

## Step 4: Public booking pages ⬜

Files: `Pages/Book/Index`, `Pages/Book/Details`, `Pages/Booking/Manage`, `wwwroot/css/site.css`, `wwwroot/js/site.js`.

Flow in section 8.1. Confirmation page and manage page by token. Customer cancellation per section 7.

Acceptance:
- A customer can complete a booking on a phone-width screen in under one minute.
- A losing race shows "that time was just taken" and returns to the time list.
- Unknown token and unknown slug return the same not-found page.
- Double-clicking the confirm button creates one booking.

## Step 5: Admin back office ⬜

Files: `Pages/Admin/*`, `Pages/Shared/_AdminLayout.cshtml`.

Dashboard (day view with complete, no-show, cancel with reason), services, staff with services and weekly hours, closures with a warning about affected bookings, settings, audit log, About page with version and build.

Acceptance:
- Every admin query is scoped to the signed-in business; a forged id from another business changes nothing.
- Closing a day does not cancel bookings and shows how many are affected.
- Role badge shown; keyboard navigation and visible focus work; empty, error and success states exist.

## Step 6: Message links (section 8.5) ⬜

Files: `Data/Migrations/002_contact_settings.sql`, entity and settings page updates, a small `MessageLinks` helper in `Services/`.

Acceptance:
- Correctly URL-encoded `sms:` and `m.me` links; "Copy message" works.
- The `sms:` form is tested on a real Android and a real iPhone and the result is written back into section 8.5.

## Step 7: Tests in CI ⬜

Files: `tests/BarberBook.Web.Tests/` (xunit), `.github/workflows/ci.yml`.

Acceptance: CI runs the self-check, the SQL invariants (PostgreSQL service container) and the integration tests on every push.

## Step 8: Free deployment ⬜

Files: `Dockerfile`, `docs/DEPLOY.md`.

Render free web service (Docker) or Cloud Run, Neon Postgres, secrets as environment variables, regular `pg_dump` backup (section 17.1).

Acceptance:
- Database is **Neon Free, PostgreSQL 16** (decided, see rules section 17.3). Create a separate Neon branch for tests. `CREATE EXTENSION btree_gist` succeeds there, and `tests/db/double_booking.sh` passes against that test branch. If not, stop and use the fallback (Supabase Free) or another database before going live, because invariant I1 depends on it.
- A scheduled `pg_dump` backup is stored outside Neon.
- Booking page works on the free host; cold start behavior is documented.

## Step 9: JSON API and Flutter ⬜

Files: `Api/` endpoints per section 3.1, problem+json errors per section 8.4, JWT plus refresh token per decision 6. Then the Flutter app.

Acceptance: the API reuses `BookingService`; no business rule is duplicated in a controller.
