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
- Registering a taken email shows a friendly error, not a 500. A shop name, an owner name or a booking address that is already used is never refused: the address gets a short code at the end (changed after the first release, see the table below).
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
| Customers page: search, visits, spend, CSV download | 6.y3 | tests | page on a real phone |
| Customer moves their own booking: `/booking/{token}/move`, same cutoff as cancel, same staff and service | 7 | tests (5 service and page tests, 4 Core checks) | page on a real phone, in Safari |
| Record a sale: time defaults to when entered, inside working hours; blocks its time while running; label "Manual input of sales" | 6.y20 | tests (3 new); migration 033 | form and label on a real phone |
| Deposit paid by hand: per service amount, snapshot on the booking, "Deposit received" mark, pay instructions in Settings, shown on the manage page | 7.1 | tests (7); migration 032 | pages on a real phone; real GCash payment |
| Reports: outcomes, revenue, by service and staff | 6.y4 | tests | page on a real phone |
| Staff move a confirmed booking (Move button, free times, audit, new reminder) | 6.y5 | tests | page on a real phone |
| Platform: extend a shop's access by 7, 30, 90 or 365 days | 6.y6 | test | Platform page on a real phone |
| Team roles: owner, manager, staff; Team page; menu and pages by role; switched-off people signed out at once | 6.y7 | tests | Team page and a staff sign-in on a real phone |
| Shop logo (Settings upload, booking page) and exact-date filter on Reports | 6.y8 | tests; migration 013 | upload from a real phone; live deploy |
| Forgot password (emailed one-hour link) and My account change password | 6.y9 | tests with a capturing email sender; migration 014 | **a real reset email through Brevo**, pages in a browser |
| Turnstile bot check on sign-up, forgot password and the customer booking form (off until keys are set) | 6.y10 | tests with a fake Cloudflare | **the real Cloudflare widget in a browser**, keys on Render |
| Platform backup card: link, last copy saved, 30-day warning | 6.y11 | tests; migration 015 | page in a browser |
| Day KPI cards on Bookings; installable back office (manifest, icons, service worker) | 6.y12 | tests | **Install app on a real Android and iPhone** |
| Renewal: reminder emails before access ends, how to pay (GCash) on trial-ended and About | 6.y13 | tests; migration 016 | **a real reminder email**, the pages in a browser |
| Staff login tied to one staff member: own bookings, reminders, follow-ups, walk-ins only | 6.y14 | tests; migration 017 | a real Staff login on a phone |
| Automatic customer emails (confirmation, reminder) with a daily limit and a paid add-on | 6.y15 | tests; migration 018 | **real emails through Brevo** to a customer, the limit in practice |
| GCash QR code upload (Platform) shown on About, Settings and trial-ended | 6.y16 | tests; migration 019 | scanning it with a phone |
| More industries: spa, nails, pet, fitness, tattoo, auto, tutor, photo (labels, starter and suggested services) | 18.1 | tests; migration 020 (widens the industry CHECK) | the sign-up list and a booking page of a new kind in a browser |
| Email confirmation before use; `/Platform` for the platform owner to switch shops on/off, confirm by hand, send a link; Brevo (or Resend) HTTP email; the check-your-email page continues by itself | 6.z | tests with a fake email sender and a fake Brevo server; migration 009 applied to a database that already had a shop | the pages in a browser (a real email through Brevo arrived on the live site, confirmed by the owner) |
| "Other" service for visits not in the list (booking and walk-in) with a typed description | 6.w | tests; migration 010 applied to a database that already had a shop | browser |
| Privacy Notice and Terms pages (draft text, with processor terms), agreement at sign-up with version and time stored, customer data export and delete for the shop | 6.v | tests; migration 011 | **review by a lawyer** (the texts are not legal advice); the pages in a browser; a clinic's actual consent flow |
| Half-day closures (only From, only Until, 12 AM as end of day); lenient booking address with a 3-character minimum | 6.x2, 18.2 | tests, Core self-check | a phone |
| `/ping` for a free uptime monitor (does not touch the database) | | test | the monitor itself |
| **AI receptionist** (paid add-on, merged in PR #1): chat on the booking page that checks free times and books through `BookingService`; switched on per shop on the Platform page; monthly conversation limit, chat rate limit, token log; Settings card | 6.y17 | 21 tests (17 + 4 SDK adapter against a fake API) on PostgreSQL 16.15; mutation check (removing the tenant filter or the limit makes the right tests fail); browser run at 375x812 with a fake API; migration 021 on a database with an existing shop | **the live Anthropic API (never called: no key in the build environment)**, real conversation quality and Tagalog, real cost per conversation, Safari and a real phone, the Platform switch in a browser, **Privacy Notice and customer data tools do not cover chats yet (must be done before switching it on for a real shop)** |
| Privacy Notice and Terms for the AI assistant (and the new kinds of business); chats included in a customer's export and delete; the manage link is added by the server instead of being sent to the AI service; a notice in the chat; the shop logo keeps its own shape (wide banners are shown wide) | 6.v, 6.y17 | tests (the export and delete by number in three formats, another shop's chat and a different number kept, a walk-in placeholder matches nothing; the link is not in anything sent to the model; Privacy and Terms text); a mutation check on the delete's shop filter | **lawyer review of the new texts**, what the AI provider keeps and where, the logo in a browser with a wide image, Safari and a real phone |
| Promo Creator (promo banner): nine modes (Halloween, Christmas, New Year, Valentine's, Summer, Back to school, Payday sale, Rainy season, Plain) each with its own colours, decoration and label; headline, optional badge and details, optional picture, a switch and a last day; a live preview while the owner types (`/Admin/Promo`); shown above the booking form; the picture is served only while showing | 6.y18 | 9 tests (validation per field and the database checks, the last day at midnight in the shop's time, words only or with a picture, keeping or removing the picture, every mode's page and style rules, words shown as text, isolation between shops, owner upload over HTTP, sign-in needed, closed shops show no promo); migrations 022 and 023 on a fresh database and ModelCheck; real browser: the live preview changes mode, text, badge, until-date, decoration and picture as the owner types, and the public page at 375px and 1280px, no console errors. Earlier a test caught that an unticked box left the banner on, now fixed | the Terms do not yet cover what a promo says or the right to the picture (next legal review), Safari and a real phone, large pictures on a slow connection, migration 023 relaxes two NOT NULL columns (no data changed) |
| Logo Creator in the Logo card of Settings: letters, shape, colours, icon and letter style with a live preview; drawn in the browser, saved as a 512 x 512 PNG through the ordinary logo upload | 6.y8 | 11 tests (the default letters from a shop's name, readable colour pairs, closed lists, the page carries every choice and the shop's colour, the style sheet has every swatch); real browser: the creator draws and previews, typed markup is escaped, "Use this logo" saved a real 512 x 512 PNG of 24 KB that the server accepted, a second design replaced it, and the logo shows on the booking page; the card is full width so the choices are not squeezed, no overflow at 1280px and 375px, no console errors | Safari (drawing an SVG into a canvas), a real phone, other letter fonts (only the system's plain and classic ones are used) |
| Promo discount and Payment total: a discount % in the Promo Creator taken off the services that are part of the promo (a "Part of the promo" box on each service), fixed on the booking when it is made (regular price, percent and net price kept); regular and net prices on the booking page, confirm page, manage page, day view, walk-in page, price list and AI assistant; a Payment total on the page after Done (starts at the quoted price) that reports and customer totals use; the customer sees Total paid; Reports show promo discounts given | 6.y18, 6.y19, 10 | 12 tests (the price at booking, never changed later and kept whatever the appointment day, services left out and Other and free services, promo off or ended or without a discount, walk-ins, the form and database checks, payment total range and scope, reports and customer totals, export, the assistant, every public page over HTTP, the checkbox forms) plus 3 Core self-checks; a mutation check (no discount, the old checkbox default, reports ignoring the payment) failed 10 of 12 tests as it should; migration 024 on a database that already had a service and a completed booking (nothing changed); ModelCheck; real browser: the live price list with rounding (33% of 150 = 100.50), a real customer booking with the promo, the manage page, the payment total saved and shown to the customer and in the report, no console errors. **Also fixes a bug that was already in `main`: unticking Active on a service or a staff member and saving left it active** (a browser sends nothing for an unticked box and the form defaulted to true); both forms now default to off and a new item starts ticked | the itemized receipt when a customer adds another service (next PR), a payment method, Safari and a real phone, how staff find the Payment total on a real phone |
| Sign-up: only the email must be unique (shop names and owner names may be shared); the booking address is made from the shop name and gets a short code at the end when it is taken (`san-juan-salon-k7x2`), never an error | 18.2 | 18 account tests (same shop and owner name allowed, the same email in any capitals refused, an address that is blank, typed, taken, too short, symbols only or very long, 12 and 8 parallel sign-ups for one name or one email) plus a pure-function test that an address with a code always fits in 40 characters and is valid; the HTTP sign-up flow; a real bug caught on the way: a longer code could push an address past the database's 40-character limit | a real phone, how the owner learns the address they got (it is in Settings and on the QR code, not on the sign-up result), old shops keep their addresses |
| Itemized receipt: staff add another service done on top of the booked one on the page after Done (own line and price, promo if running today and part of it, "Other" with a typed price), remove it, and the payment total follows; the customer sees each line and Total paid; Reports list service prices and show tips/other changes separately; money changes are audited | 6.y19 | 12 tests (adding to a completed visit only, unknown and other-shop services, the Other rules, the limit of 10, the promo on added lines, the payment total going back to the items total with the audit trail, shop and staff scope plus the composite key and the cascade in the database, reports and customer totals, discounts given, export and delete, and the staff page and the customer's receipt over HTTP); a mutation check (no reset of the payment total, reports ignoring the services, no completed-only rule) failed 6 of the 12 as it should; migration 025 on a database that already had a completed booking (nothing changed); ModelCheck; real browser on a clean build: a real customer booking, a Hair color and an Other line added, a tip typed, the customer's receipt, the line removed (the total went back to 450.00) and the report. A first browser run used a build left over from the mutation check and showed stale numbers: the source was clean, it only needed a rebuild | adding the services to the customer's history page for staff, a payment method, Safari and a real phone |
| Manual amounts setting: Settings switch "Staff can type the payment total and the price of an added service" (default on, migration 026); a typed Amount for any added service (blank = listed price with promo; typed = no promo on top); when off the Done page shows the total as fixed, the server refuses typed totals and amounts (forged posts too) and a saved total can be put back to the items total | 6.y19 | 3 new tests (service refusal and clearing, per-shop scope, the Settings checkbox with an unticked box and a forged post over HTTP, typed amounts on any added service) and 1 changed (a typed price is no longer ignored for a listed service); a mutation check (guard removed) failed both Settings tests as it should; full suite, ModelCheck and migration 026 results are in the commit message | Safari and a real phone, a payment method |
| Day at a glance cards are clickable: Still to do, Done, Earned, No-shows and Cancelled each open a list of that day's bookings with time, service and price, payment, staff, customer and phone, under the cards (`?kpi=`; click again to close) | 6.y19 (day view) | 1 HTTP test (closed by default, the list for a card, empty card, an unknown card opens nothing, another shop's booking never shows); full Web suite below. Not checked in a real browser or on a phone | look on a real phone |
| Done-page and walk-in tidy-up: the Medical record card shows only for clinics (or when a record already exists); the Follow-up visit card says optional and a blank date is accepted; "What was done" on Add another service shows only when Other is chosen; the walk-in service is a dropdown | 6.y19, 18 | existing tests updated (barbershop has no record card, clinic has, blank follow-up date accepted, the Other box is marked for the script); full Web suite 299/299. Not checked in a real browser or phone | a real browser and phone look |
| Record a sale by hand: Settings switch (off by default, migration 027) and a Record a sale page for a manager: service, amount, date, staff, customer or walk-in; saved as a completed visit that reports and customer history count; audited | 6.y20 | 6 tests (off until switched on, the sale counted by reports and the customer list, walk-in customers, input checks and nothing saved on error, double send and other-shop scope, the Settings checkbox and the page over HTTP); full Web suite 305/305. Not checked in a real browser or phone | a real browser and phone look |
| AI limits per shop and cost monitor: /Platform/Ai lists each shop with the add-on or usage, one line each (conversations, tokens, estimated cost in PHP and USD), searchable, month by month, with the shop's own limits per day, per month and tokens (migration 028) | 6.y17 | 5 tests (day and month limits and a refusal counting nothing, tokens limit, range checks and the database check, summary numbers cost and search, platform-owner-only over HTTP); full Web suite 310/310; ModelCheck; real browser at phone width with 12 shops (list, open a line, save a limit, no console errors). **Cost uses placeholder prices from settings: not the real price of the model; the live API was never called** | real prices in settings, a real phone |
| Platform shop list as summary lines: one line per shop (name, on/off, email pending, AI add-on, owner email and address); open a line for the details and the buttons; a search box with a suggestions list (a datalist of every shop) that narrows the list by shop, address, owner or email | 6.z | 1 HTTP test (summary lines, suggestions list every shop, the search narrows the list, empty result); full Web suite 311/311; real browser with 11 shops (collapsed at load, open a line, search by a suggestion, no console errors). Not on a real phone | a real phone |
| Platform: when each client ends and a heads-up (each shop line shows when its access and add-ons end, amber when due; a notice on /Platform and on every back-office page for the platform owner when a client's access or add-on ends within 7 days or ended within 14; `?expiring=true` shows only those); an opened shop is a Subscription table (one row each for Access, AI add-on, Email add-on with its end date and an Add days control), Switch off on the right, the GCash QR and database cards moved below the shop list | 6.z | 2 tests (the due window and the wording; the line, the notice, the filter, the notice shown only to the platform owner, the cards below the list); full Web suite; real browser at desktop and phone width. The heads-up is shown on screen only: **no email or push to the platform owner yet** (clients still get their own emails at 7, 3 and 1 days) | an email to the platform owner |
| Look and feel pass (owner pages): search boxes styled like the other fields, link-style secondary buttons drawn as outline buttons (they were solid), the day navigation on one line, summary cards three to a row on a phone, the cancel form of each booking folded under 'Cancel this booking...' | none | existing tests (full Web suite); Chromium at phone width on Bookings, Customers, Services, Settings, Reports and the booking page, no horizontal overflow except the Customers table, which scrolls sideways inside its card. Not checked on a real phone or Safari | a real phone; the customers table on a small screen |
| Help and guide: a Help page in the menu with a topic for every feature (what it is, numbered steps in simple words, a tip, a button to open the feature), searchable, showing only the topics the signed-in level can use; a 'How does this work?' link under the title of 15 pages opens its topic | none | 4 tests (every topic complete and unique, topics by level, every page link names a real topic, the page, search, opening one topic and the 'Open' buttons never leading to a missing page); full Web suite 317/317; Chromium at phone width. The guide is English only (Tagalog later, like all text) and must be updated in the same commit as a feature | Tagalog text; screenshots or short videos in the guide |
| Send a promo to customers: a promo-consent box on the booking form and the walk-in form (never pre-ticked), a yes or a stop recorded by staff in the customer's history (audited), and a Send a promo page that lists only the customers who said yes and can be reached, with the ready message (name, promo, discount, last day, booking link, STOP line), Copy / Send as text / Email buttons and Mark as sent; the system sends nothing (migration 029) | 6.y21 | 6 new tests and 1 public-form test (consent only from a tick and never taken away by a later booking, who is listed and what is counted, sent marks, wildcards in the search, audit and other-shop scope, the message text, the pages over HTTP including a forged mark and a stop); full Web suite 323/323; ModelCheck; Chromium at phone width. **Legal: marketing messages need the lawyer's review of the consent wording and the Privacy Notice (LEGAL_BRIEF)** | automatic bulk email with an unsubscribe link, a record of each message sent |
| Automatic promo email: one button on the Send a promo page; a background worker emails the promo to customers who agreed and have an email, within the daily email limit (goes on the next day when it is used up), marks each sent, stops by itself (everyone sent, promo ended, customer emails off) and every email has an unsubscribe link and page (migration 030) | 6.y21 | 6 tests (who gets it and once each with a working unsubscribe link, the daily limit over several days, stopping when the promo ends or the switch is off and skipping someone who just stopped, the start conditions and a refused email retried, the unsubscribe link audited and safe to repeat or guess, the public page and the card over HTTP); full Web suite 338/338; ModelCheck. **Not verified: a real email delivered through Brevo or Resend (no key here), how the email looks in a real inbox, spam filtering, and the one-click unsubscribe header (not built)** | a real test with the shop's own email; the email's look; SMS |
| Billing and prices: prices from settings (Pro 499 a month or 4,990 a year, AI and email add-ons as placeholders), a Plan and billing page for the owner (plan, end date, prices, GCash QR, 'I paid' with the reference number, payment history, printable confirmation), a Payments page for the platform owner (confirm, which extends the access or add-on and gives a receipt number, reject, record by hand, ledger and the month's total, an alert on every page while payments wait), the billing page still open after access ends; default token limit for the AI (migration 031) | 6.y22 | 7 tests (prices from settings, the 'I paid' checks and one reference pays once, confirming extends from the later date and works once, a lapsed shop and the add-ons and a shop with no end date, rejecting and recording by hand, totals and receipt scope and the database checks, the whole flow over HTTP including who can see what and the ended-access page); full Web suite 345/345; ModelCheck; Chromium at phone width. **Not verified: a real GCash payment end to end, the QR on a real phone, real AI cost (the prices of the add-ons are placeholders), and the wording of the confirmation with an accountant (it says it is not a BIR receipt)** | a gateway (PayMongo or Xendit), a public pricing page, the AI price from real cost |
| Send in other messaging apps: every ready message (promo, reminders, follow-ups, thank-you) gets an 'Or send in an app' row with WhatsApp (this customer's chat, message typed in), Viber and Telegram (share screen with the message typed in) and More apps (the phone's share list, for Messenger and others); phone numbers become international digits | 6.y21 | 10 new tests (the number conversion cases, the links on the send page); full Web suite 332/332; Chromium at phone width. **Not checked: that the WhatsApp, Viber and Telegram links open the real apps and keep the message (needs a real phone with each app installed); Telegram's link form is the least certain** | a real phone check of each app; a Messenger link if a way to type the message in appears |

Not built yet: AI receptionist on Messenger and Viber (needs Meta App Review first), customer messages (confirmation and reminder links), customer list and export, reports, reschedule, MANAGER and STAFF roles, logo and cover photo, online payment, SMS.
