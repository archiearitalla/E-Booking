# Build plan

Ordered steps to finish the MVP. Each step lists the files to touch and the acceptance criteria that prove it works. Do not move on until the current step's criteria pass. Behavior comes from `BUSINESS_RULES.md`; section numbers below refer to it.

Legend: ✅ done and verified · 🟡 written, not compiled · ⬜ not started

## Step 0: Make the existing code build ✅

Verified on Windows, .NET SDK 8.0.426 and 10.0.401 installed, PostgreSQL 16.15: build 0 warnings 0 errors; self-check 55/55; `double_booking.sh` 20/20; `tests/VeskaFlow.Web.ModelCheck` confirms the EF model matches the migrated schema and a booking round-trips through EF. The Data files needed no fixes. A placeholder `Program.cs` (with `/healthz`) was added so the project builds; Step 1 replaces it.

Files: `VeskaFlow.sln`, `src/VeskaFlow.Web/*`

1. `dotnet restore` (needs NuGet access).
2. Add the web project: `dotnet sln add src/VeskaFlow.Web`. It has no entry point yet, so also create a minimal `Program.cs` in Step 1 before building it.
3. Fix any compile errors in `Data/Entities.cs`, `Data/AppDbContext.cs`, `Data/SqlMigrator.cs`, `AppInfo.cs`.

Acceptance:
- `dotnet build VeskaFlow.sln` succeeds with no warnings treated as errors.
- `dotnet run --project tests/VeskaFlow.Core.SelfCheck` prints "All 55 checks passed."
- `tests/db/double_booking.sh` prints "All database invariants hold."
- `AppDbContext` model matches the SQL: write a small check that opens the context and runs `db.Bookings.Take(1)`, `db.Staff...` etc. against a migrated database without a column-name error.

## Step 1: Program.cs and startup ✅ (parts unverified)

Verified: empty database gets all tables on first start, second start applies nothing; `/healthz` 200 with version, and 503 after the database was dropped while the app ran; security headers present on responses; `/Error` renders. Verified in Step 3: the `auth` rate-limit policy returns 429 and the `/Admin` folder policy redirects to `/Login`. Still unverified: the `booking` policy (not attached until Step 4), HSTS (HTTPS only), forwarded headers (`Proxy:TrustForwardedHeaders`, off by default).

Files: `src/VeskaFlow.Web/Program.cs`, `appsettings.json`, `appsettings.Development.json`, `Pages/Error.cshtml`.

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

## Step 2: BookingService ✅

Files: `Services/BookingService.cs`, `Services/BookingResults.cs`.

Implements sections 6, 7 and 8: `GetFreeStartsAsync`, `CreateAsync` (any-available assignment, idempotency, retry on `23P01`), `TransitionAsync` (state machine, policy, audit row, `xmin` concurrency).

Acceptance (integration tests against a real PostgreSQL):
- 50 parallel `CreateAsync` for the same slot: exactly one success, the rest `SlotTaken`, no exception escapes.
- "Any available" with two staff: two parallel requests for one time both succeed on different staff; a third gets `SlotTaken`.
- Same idempotency key twice returns the same booking and creates one row.
- Lead time, horizon, blackout, buffer and working hours are enforced on the server even when the posted start time came from a stale page.
- Customer cancel after the cutoff is refused; staff cancel succeeds; every change writes an audit row.
- A booking id from another business returns not found.

Verified: all of the above in `tests/VeskaFlow.Web.Tests` (21 xunit tests, real PostgreSQL 16, 25 consecutive runs without a failure after the fixes below). Findings while testing: (1) concurrent inserts into the exclusion constraint can deadlock (40P01, wrapped by EF in `InvalidOperationException`) and 50 at once exceeded the 30 s command timeout, so attempts for one staff member are queued with `pg_advisory_xact_lock`; the constraint is still the guarantee. (2) A retry with the same idempotency key that lost the slot race now returns the existing booking instead of `SlotTaken`. Interpretation to confirm: a staff walk-in may start in the slot that is in progress right now (up to one slot length ago). Not verified: HTTP layer (no pages yet), Held/Pending flows beyond the state machine, Neon behaviour of advisory locks.

## Step 3: Onboarding and sign-in ✅

Files: `Services/OnboardingService.cs`, `Services/AuthService.cs`, `Pages/Register`, `Pages/Login`, `Pages/Logout`.

Register creates business, owner, one staff, one service and Mon-Sat 09:00-18:00 hours in one transaction. FREE plan allows 1 staff (enforce in the staff page too).

Acceptance:
- Registering a taken slug or email shows a friendly error, not a 500.
- Wrong password and unknown email produce the same message and similar timing.
- After sign-in, `User` has `business_id`, role and display name claims. Sign-out clears the cookie.
- More than the allowed attempts per minute returns 429.

Verified: all of the above in `tests/VeskaFlow.Web.Tests` (41 tests now, 20 of them for Step 3; run 5 times in a row without a failure; the HTTP ones drive the real app in-process against PostgreSQL 16) and by starting the real app under Kestrel (pages 200, `/Admin` redirects to `/Login`). Also covered: parallel registrations for one slug give one success and friendly errors; the password is stored as a PBKDF2 hash; open redirects through `ReturnUrl` are ignored; a form post without the antiforgery token is rejected; the auth cookie is `HttpOnly` and `SameSite=Lax`. Notes: the 429 limit counts form posts only (viewing the form is free); `Pages/Admin/Index` is a placeholder until Step 5; `PlanLimits` holds the 1-staff FREE limit for the staff page in Step 5. Not verified: the `Secure` cookie flag (needs HTTPS), real-browser rendering, accessibility checks, and "similar timing" is a loose 0.5x to 2x bound on PBKDF2 time, not a side-channel analysis.

## Step 4: Public booking pages ✅ (one criterion only partly verified)

Files: `Pages/Book/Index`, `Pages/Book/Details`, `Pages/Booking/Manage`, `wwwroot/css/site.css`, `wwwroot/js/site.js`.

Flow in section 8.1. Confirmation page and manage page by token. Customer cancellation per section 7.

Acceptance:
- A customer can complete a booking on a phone-width screen in under one minute.
- A losing race shows "that time was just taken" and returns to the time list.
- Unknown token and unknown slug return the same not-found page.
- Double-clicking the confirm button creates one booking.

Routes: `/b/{slug}` (service, barber, date, times), `/b/{slug}/confirm` (details form, one idempotency key per form), `/booking/{token}` (manage and cancel, no-store, noindex). Empty 4xx/5xx responses render one page per status (`/Status/{code}`), so an unknown slug, token or URL return the identical 404 page. The `booking` rate limit counts posts only.

Verified: 12 new HTTP tests (53 in total, three consecutive full runs without a failure): full booking flow, privacy of the public page, parallel double submit gives one booking, a race lost to another customer returns to the time list with "That time was just taken", stale confirm link redirects to the time list, identical not-found pages, a service id from another shop is not found, field errors create nothing, customer cancel before the cutoff works, after the cutoff it is refused with the shop's cutoff and a forged post changes nothing, booking posts get 429, manage page headers. In a real browser at 375x812 (built-in browser pane): registered a shop, listed times, booked, and a genuine double-click on Confirm created exactly one booking (checked in the database).

Not verified: the "under one minute" criterion was not timed with a stopwatch (the flow is three screens and four typed fields, and took seven browser actions); only Chrome-based pane tested, no Safari or real phone; no automated accessibility audit (labels, focus styles and ARIA are in place); the radio's accessible name was not inspected with a screen reader; the `Secure` cookie flag still needs HTTPS. Message links ("Message the shop") are Step 6.

## Step 5: Admin back office ✅ (accessibility only partly verified)

Files: `Pages/Admin/*`, `Pages/Shared/_AdminLayout.cshtml`.

Dashboard (day view with complete, no-show, cancel with reason), services, staff with services and weekly hours, closures with a warning about affected bookings, settings, audit log, About page with version and build.

Acceptance:
- Every admin query is scoped to the signed-in business; a forged id from another business changes nothing.
- Closing a day does not cancel bookings and shows how many are affected.
- Role badge shown; keyboard navigation and visible focus work; empty, error and success states exist.

Built: `/Admin` (day view; complete, no-show, cancel with reason; stale forms refused), `/Admin/Services` (+ edit; deactivate, never delete), `/Admin/Staff` (+ edit with service links and weekly hours, two windows per day, FREE plan limit of one active staff member enforced under an advisory lock), `/Admin/Closures` (whole-day closures for the shop or one person), `/Admin/Settings`, `/Admin/Audit`, `/Admin/About`. Services: `AdminDayService`, `CatalogAdminService`, `ShopSettingsService`; every method takes the business id from the signed-in user's claims (`AdminPageModel`), never from the request.

Verified: 30 new tests (83 in total; five consecutive full runs without a failure after fixing one flaky test of my own, which had compared a global row count while other tests ran in parallel). Service level: tenant scoping of services, staff, hours, closures, settings, day view and audit; plan limit including six parallel adds; hours and closures count affected bookings and cancel none; closure blocks new bookings on that day. HTTP level: every admin page redirects anonymous users to sign-in; a forged id from another business returns 404 and leaves services, staff, hours, bookings and closures unchanged (eight endpoints tried: two reads and six writes); the day view and audit never show another business's data; cancel with reason writes the booking, the `STAFF` audit row and shows in the audit page; closing a day with a booking keeps it and shows "1 existing booking(s) ... not cancelled"; role badge, skip link, labelled nav landmark and `aria-current` on every page; empty, error and success states. In a real browser at 375x812: dashboard, closure warning, first Tab lands on the skip link with a 3px focus outline, nav buttons are 40px+ tall.

Not verified: full keyboard-only walkthrough of every form; screen reader behaviour; contrast checked by eye only; Safari and real phones; the day view and forms were not load tested. Known limits: a signed-in cookie stays valid until it expires even if the user were deactivated (no user management exists yet); only the OWNER role exists, MANAGER and STAFF pages are not built; closures are whole days only; walk-in (staff-created) bookings have no UI yet; "Message customer" is Step 6.

## Step 6: Message links (section 8.5) ⬜

Files: `Data/Migrations/003_contact_settings.sql`, entity and settings page updates, a small `MessageLinks` helper in `Services/`.

Acceptance:
- Correctly URL-encoded `sms:` and `m.me` links; "Copy message" works.
- The `sms:` form is tested on a real Android and a real iPhone and the result is written back into section 8.5.

## Step 7: Tests in CI ⬜

Files: `tests/VeskaFlow.Web.Tests/` (xunit), `.github/workflows/ci.yml`.

Acceptance: CI runs the self-check, the SQL invariants (PostgreSQL service container) and the integration tests on every push.

## Step 8: Free deployment 🟡 (live on Render and Neon; domain, backups and real-phone check still open)

Files: `Dockerfile`, `.dockerignore`, `render.yaml`, `docs/DEPLOY.md`. Written, and the Release publish was run in Production mode on a PORT from the environment (health check and a booking page answered). The Neon safety gate passed (PostgreSQL 18.6, see rules 17.3.1). Not done: the Docker image build (no Docker on this machine), a Render deploy, the backup job. Those need the owner's accounts; the steps are in `docs/DEPLOY.md`.

Render free web service (Docker) or Cloud Run, Neon Postgres, secrets as environment variables, regular `pg_dump` backup (section 17.1).

Acceptance:
- Database is **Neon Free, PostgreSQL 16** (decided, see rules section 17.3). Create a separate Neon branch for tests. `CREATE EXTENSION btree_gist` succeeds there, and `tests/db/double_booking.sh` passes against that test branch. If not, stop and use the fallback (Supabase Free) or another database before going live, because invariant I1 depends on it.
- A scheduled `pg_dump` backup is stored outside Neon.
- Booking page works on the free host; cold start behavior is documented.

## Step 9: JSON API and Flutter ⬜

Files: `Api/` endpoints per section 3.1, problem+json errors per section 8.4, JWT plus refresh token per decision 6. Then the Flutter app.

Acceptance: the API reuses `BookingService`; no business rule is duplicated in a controller.

## Branding: per-shop accent color ✅ (browser check of the settings page still open)

Files: `Data/Migrations/002_brand_color.sql`, `BrandColors` in `Services/ShopSettingsService.cs`, `Pages/Admin/Settings`, `data-theme` rules in `wwwroot/css/site.css`, `Pages/Shared/_Layout.cshtml`.

A shop picks one of six preset colors (teal, blue, orange, rose, violet, slate) in Settings; its public booking, confirm and manage pages use it. Presets rather than free hex because the CSP forbids inline styles and a closed list cannot carry markup. The database CHECK enforces the list.

Verified: solution builds with 0 warnings; Core self-check 55/55; the Web test suite passes against PostgreSQL 16.11 (90 tests, including the preset-only check, the database CHECK and the per-shop theme on the public page). Not verified: the settings page and the five new colors in a browser, contrast of the new colors in a checker, Safari. Logo and cover photo are not built (they need an image storage decision).

## Industries and the 30-day trial ✅ (browser check of Register and the banner still open)

Files: `Data/Migrations/003_industry_trial.sql`, `Services/Industries.cs`, `OnboardingService`, `AuthService` (trial claim), `Pages/Register`, `Pages/Shared/_AdminLayout` (banner), `BookingService.CreateAsync` and `ShopCatalogService` (booking closes after the trial), `appsettings.json` (`Trial:Days`).

Verified: the Web test suite passes against PostgreSQL 16.11 (90 tests), including `TrialAndIndustryTests` (clinic starter service and trial end date, no trial by default, unknown industry refused, customers refused and staff still allowed after the trial, the public page reports it). Not verified: the Register page and the trial banner in a browser, the claim after a real sign-in, the new migrations on Neon.

Test database used: a portable PostgreSQL 16.11 on port 55432, separate from the machine's own PostgreSQL.

### Step 8 result (2026-10-25)

Deployed to Render (free, Singapore, built from the `Dockerfile`) with Neon (Free, Singapore, PostgreSQL 18.6). `GET /healthz` returns `{"status":"ok","version":"0.1.0"}`, `/Register` returns 200, and the security headers (CSP, HSTS, X-Frame-Options, X-Content-Type-Options) are present. The Docker image build is verified by the Render build itself.

The first two attempts failed: the connection string was pasted in URL form (Npgsql cannot read it, and the old code echoed it into the log, so the Neon password was reset), and the service had been created in Oregon instead of Singapore. Both are fixed and recorded in `docs/DEPLOY.md`. Blueprint Auto Sync is set to No, so a push does not redeploy by itself; use Manual Deploy or Manual sync.

Still open: a registration and booking run on a real phone against the live link, the `pg_dump` backup and one restore, a first-request time after idle (not measured), and behaviour behind the Cloudflare proxy. The custom domain `flow.veskacode.com` is set up (DNS only) and answers over HTTPS.

## Features added after Step 8 (all in `main`; Web tests 164 of 164 and Core self-check 65 of 65 on PostgreSQL 16.11 unless noted)

| Feature | Rules | Verified | Not verified |
|---|---|---|---|
| Trial lapse: after the 30 days the back office is locked and the public page closed; nothing deleted | 18.2 | tests, browser on localhost | live deploy |
| A trial has no staff limit | 18.2, 12 | tests | live deploy |
| Walk-in form at `/Admin/WalkIn`, phone optional | 8.2 | tests, browser on localhost | Safari, real phone |
| Limits on the open booking page: one person, overlap, cap of 2, honeypot, rate limit | 8.6 | tests including parallel requests, real app on localhost | a person using a different number each time is not caught |
| QR code for the booking link in Settings (download SVG, print) | 8.7 | tests | scanning a printed code with a real phone |
| Bookings filter: Confirmed (default), Cancelled, Done | | tests, browser on localhost | live deploy |
| Closed days that repeat every week (on/off switch), one Closures page for both kinds | 6 | tests; migration `004_weekly_closure.sql` applied to the test database | the Closures page in a browser, Neon, live deploy |
| Versioned CSS and JS URLs, so a deploy is never served stale files | | test | |
| Name moved from every page to About; header bell for the trial | | tests, browser on localhost | |
| Closures with time windows (one-off and every day / every weekday), refused outside opening hours; AM/PM time pickers | 6.x | tests; migration 004 | browser, phone, live deploy |
| Opening hours in Settings: one range with a break for all days, copied to every staff, a hard limit on booking; kept day-offs become closures | 6.x | tests; migration 005 | browser, live deploy |
| Suggested services per industry; collapsible services list; service dropdown on the booking page | 18.1 | tests | browser |
| Follow-ups: date and service on a completed booking, Follow-ups page, reminders in the bell and a strip (days before is a setting), ready message with text, email, share, copy, save contact | 6.y | tests; migrations 006, 008 | sms:, mailto:, share sheet and .vcf import on a real phone; browser |
| After the visit page (opens on Mark completed): optional medical record, follow-up, thank-you message; customer history | 6.y | tests; migration 007 | browser; access control is only "signed in to this business" (no staff roles yet) |
| Mark completed / no-show only today and after the scheduled time (buttons hidden otherwise); staff cancel needs a reason; confirm before cancel, no-show, customer booking and cancel | 7 | tests, Core self-check | the confirm dialogs in a real browser |
| Booking confirmation: copy link, add to calendar (.ics) | | tests | a real phone calendar |
| Customer reminders: Reminders page, bell and strip, reminder hours setting (default 24), ready message with manage link, mark reminded | 6.y2 | tests; migration 012 | page and message on a real phone; live deploy |
| Email confirmation before use; `/Platform` for the platform owner to switch shops on/off, confirm by hand, send a link; Brevo (or Resend) HTTP email; the check-your-email page continues by itself | 6.z | tests with a fake email sender and a fake Brevo server; migration 009 applied to a database that already had a shop | the pages in a browser (a real email through Brevo arrived on the live site, confirmed by the owner) |
| "Other" service for visits not in the list (booking and walk-in) with a typed description | 6.w | tests; migration 010 applied to a database that already had a shop | browser |
| Privacy Notice and Terms pages (draft text, with processor terms), agreement at sign-up with version and time stored, customer data export and delete for the shop | 6.v | tests; migration 011 | **review by a lawyer** (the texts are not legal advice); the pages in a browser; a clinic's actual consent flow |
| Half-day closures (only From, only Until, 12 AM as end of day); lenient booking address with a 3-character minimum | 6.x2, 18.2 | tests, Core self-check | a phone |
| `/ping` for a free uptime monitor (does not touch the database) | | test | the monitor itself |

Not built yet: customer messages (confirmation and reminder links), customer list and export, reports, reschedule, MANAGER and STAFF roles, logo and cover photo, online payment, SMS.
