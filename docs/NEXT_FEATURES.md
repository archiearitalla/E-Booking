# VeskaFlow: next features (brief for another Claude session)

Read `CLAUDE.md` and `docs/BUSINESS_RULES.md` first. They decide behavior. Change rules and code in the same commit. Section 4 of the rules (invariants) is non-negotiable.

## Context

- VeskaFlow is a multi-tenant booking SaaS for 11 kinds of business (barbershop, salon, clinic, spa, nails, pet, fitness, tattoo, auto, tutor, photo). ASP.NET Core 8 Razor Pages, EF Core with Npgsql, PostgreSQL. Hosted free: Render + Neon + Cloudflare at `https://flow.veskacode.com`.
- Free first: no paid service, no SMS gateway. Staff send messages by hand. Subscription is paid by hand through GCash.
- The owner is Filipino and tests on a real phone. Everything must work well at phone width.

## Rules that apply to every feature

- Pages call services; services call `VeskaFlow.Core`. No business logic in pages.
- Every back-office query is filtered by the `BusinessId` from the signed-in claims. Wrong tenant, unknown id and missing token all return not found.
- Times are UTC in code and the database. The business time zone only interprets local times.
- Database changes: a new numbered embedded SQL file in `src/VeskaFlow.Web/Data/Migrations/` (next number is 032; 001 to 031 are applied). Additive only. Never edit an applied migration. No `DROP` or `TRUNCATE`.
- Double-booking is stopped by the database exclusion constraint. Translate `23P01` into a normal "slot taken" result, never a 500.
- All user text goes in `src/VeskaFlow.Web/Text.cs`, in English, never duplicated. Font Awesome icons only, no emoji.
- CSP is `script-src 'self'`: no inline JavaScript, no `onclick` or `onchange` attributes. Use `data-confirm` for confirmation dialogs (every button that changes a record asks first).
- Staff logins tied to one staff member see only that member's work (`onlyStaffId` parameters). Keep that scoping when adding anything that lists bookings or customers.
- Customer email is optional. Never make a flow depend on it unless the owner decides otherwise (see item 4).
- No secrets in git. Nothing that needs a paid service.
- Each feature needs tests in `tests/VeskaFlow.Web.Tests` (real PostgreSQL, see `CLAUDE.md` for the command), a docs update, and its own commit. Tests that use "tomorrow" must use `Seed.NextOpenDay()`, because Sunday is closed by default.
- Mark anything not compiled or not run as unverified in the commit message and `docs/BUILD_PLAN.md`. Do not claim "done" with failing or unrun tests.
- Start with `git pull --rebase origin main`. Never force-push `main`.

## Items, in order

### 1. Customer can move their own booking (DONE: `/booking/{token}/move`, rules in section 7)

The text below is the original brief, kept for reference. Skip this item.

**Already built:** `/booking/{token}` (`Pages/Booking/Manage`) shows the booking, offers a calendar file and lets the customer cancel (cutoff applies). **Missing:** moving.

- Add a "Move my booking" step on the manage page. Reuse `BookingService.RescheduleAsync` and `GetRescheduleStartsAsync`: same staff member and service, only free slots, re-checked on the server.
- Respect the same cutoff as cancel. After it, show the shop contact instead of the button.
- Make sure the manage link is in the confirmation and reminder emails.
- Wrong or unknown token returns not found. Rate limit with the Booking policy. The audit row says the customer moved it.

**Acceptance:** tests for allowed move, move into a taken slot (clean "slot taken"), before and after the cutoff, wrong token, token of another booking.

### 2. Deposit or advance payment (manual first)

Note: `BUSINESS_RULES.md` already describes an online deposit with a held slot (PENDING, TTL, idempotent, invariant I6) for a later PayMongo or Xendit step. Keep the manual version consistent with that design so it can be upgraded later.

**Goal:** reduce no-shows without a payment gateway.

- Per service: an optional deposit amount (0 means none). The booking page shows the amount and the shop's GCash QR/number (reuse the existing QR image pattern, see `PlatformImageService` and the `/pay-qr` endpoint; the shop will need its own QR, so add a per-business image next to the logo).
- The customer pays outside the app and the booking stays "pending deposit" until staff mark it received. Decide, and write in `BUSINESS_RULES.md`, whether an unpaid pending booking holds the slot and for how long, and what happens when the hold expires.
- Staff get a "Mark deposit received" button with `data-confirm`, and a refund-or-keep note on cancel.

**Acceptance:** rules written first; state machine changes covered by `VeskaFlow.Core.SelfCheck` if the state machine is touched; tests for hold expiry and for double-booking while a hold exists.

### 3. Waitlist

**Goal:** when a day is full, the customer leaves a name and contact, and the shop sees who wants a slot.

- Public: "Notify me if a slot opens" on a full day (name, phone, optional email, service, staff or any, date).
- Back office: a Waitlist list scoped like other lists. When a booking is cancelled or moved, show the matching waitlist entries so staff can message them by hand. If the customer gave an email and the daily email quota allows, send one automatic notice through `CustomerMailer` and the existing quota logic.
- Entries expire after their date. Add a "Remove" button with confirm.

**Acceptance:** tests for tenant isolation, staff scoping, expiry and the quota stopping the email.

### 4. Customer email required at booking (needs the owner's decision first)

Ask the owner before building. If yes: make email required on the public booking form with clear validation text, keep walk-ins in the back office optional, and explain why on the form ("for your confirmation and reminder"). Existing customers without an email must still work.

### 5. Per-staff time off (check the gap first)

**Already built:** one-off closures and repeating closures (one weekday or every day, whole day or a time window such as lunch), for the whole shop or one staff member, with an on/off switch (page `Admin/Closures`). Check whether a per-staff day off over a date range is already covered. Build something only if a real gap remains; otherwise delete this item.

### 6. Repeat bookings (weekly)

For tutors and fitness: "repeat weekly for N weeks" when staff create a booking. Create the bookings one by one with the normal checks. Report which dates failed because the slot was taken, and book the rest only if the owner chooses to. Never bypass the exclusion constraint.

### 7. Packages / memberships

A customer holds N sessions that go down by one for each completed visit. Needs rules in `BUSINESS_RULES.md` first: expiry, refunds, what counts as a used session, transfer between staff. Payment stays manual.

### 8. Photos

Photo for each staff member and service, plus a small gallery on the booking page. Reuse the sniff-and-limit approach of `LogoService` (PNG, JPEG, WebP, small size limit) and the ETag image endpoint pattern. Keep storage inside PostgreSQL so no paid storage is needed, and keep the size limit low because Neon's free tier is small.

### 9. Tagalog

`Text.cs` is the single place for text. Introduce a language switch only if it can be done without duplicating strings (for example a second resource per key) and with English as the fallback. Per-shop default language, customer can switch on the public page. Do not machine-translate legal text.

## Read first

`PROJECT_SUMMARY.md` (repo root) describes the current state: promo messages and email, AI receptionist, manual billing with receipts, platform tools, Help page, 345 Web tests on PostgreSQL 16. Update it when you finish an item. Already built, so do not redo: record a sale by hand, promo creator, closures with time windows, customer cancel page, billing page. Also not built and not in this brief: inventory, staff commission, permanent free plan, public pricing page.

## Not for this brief

- Automatic subscription billing (PayMongo or GCash API): wait until there are paying clients.
- Native Flutter app and `/api/v1`: later.
- Lawyer review of Privacy and Terms: done by a human, see `docs/LEGAL_BRIEF.md`.

## Still unverified on real devices (do not claim these work)

QR scan and "Open GCash" on Android, PWA install on a phone, Staff and Manager login on a phone, real Brevo emails (reset, expiry reminder, customer reminders), Turnstile widget, and the booking pages of the eight newest industries.

## How to work

One item per commit, in the order above, stopping after each one to report what was verified. Run the full Web test suite before every push and report the real pass count.
