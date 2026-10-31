# VeskaFlow: project summary (hand-off)

A plain summary of the whole system, written to give another session or person the full picture. Read `CLAUDE.md` and `docs/BUSINESS_RULES.md` for the rules (the rules decide behavior; if code and rules disagree, stop and raise it).

Last updated at PR #25 (merged to `main`). Tests at that point: Core self-check 85 of 85, Web 345 of 345 on PostgreSQL 16.

## 1. What it is

VeskaFlow is a multi-tenant booking system for barbershops, salons, clinics and similar service shops in the Philippines.

- Live site: https://flow.veskacode.com (Render, free instance kept awake by an uptime monitor on `/ping`).
- Repository: `archiearitalla/e-booking` on GitHub. `main` is the source of truth.
- Stack: ASP.NET Core 8 Razor Pages, PostgreSQL 16 (Neon), EF Core with Npgsql, SQL migrations embedded in `src/VeskaFlow.Web/Data/Migrations/` (001 to 031, additive only).
- Principle: **free first**. No paid service until paying clients exist. The system sends nothing to customers by itself unless a shop switches a feature on (confirmation and reminder emails, promo email).

## 2. Layout

```
src/VeskaFlow.Core/          Pure C#, no NuGet. Availability, time zones, state machine, pricing (PromoPricing), password hashing, slugs.
src/VeskaFlow.Web/           ASP.NET app: Data/ (entities, DbContext, SQL migrator, Migrations), Services/, Pages/ (Admin, Platform, Book, Booking), wwwroot/.
tests/VeskaFlow.Core.SelfCheck/   Dependency-free runner for Core (85 checks).
tests/VeskaFlow.Web.Tests/        xunit against a real PostgreSQL (345 tests).
tests/VeskaFlow.Web.ModelCheck/   Checks the EF model against the schema.
docs/                        BUSINESS_RULES.md (spec), BUILD_PLAN.md (what is built and verified), LEGAL_BRIEF.md (questions for a lawyer), DEPLOY.md, BACKUP.md.
```

Rules for code: business logic lives in services (never in pages); Core has no NuGet or EF types; every back-office query is filtered by the signed-in user's `BusinessId`; times are UTC in code and database; double-booking is stopped by a database exclusion constraint (PostgreSQL `23P01` becomes a normal "slot taken" result); all user-facing text is English and kept in `Text.cs`.

## 3. Who uses it

| Who | Where | What they do |
|---|---|---|
| Customer | `/b/{slug}` (booking page), `/booking/{token}` (manage) | Book, see the price (regular and promo price), cancel or move, chat with the AI receptionist (paid add-on). |
| Staff, Manager, Owner of a shop | `/Admin/...` | Run the day. Levels: Staff (bookings, walk-ins, reminders, follow-ups), Manager (also services, staff, closures, customers, reports, audit, record a sale, send promo), Owner (also settings, promo creator, team, billing). |
| Platform owner (you) | `/Platform`, `/Platform/Ai`, `/Platform/Payments` | Shops list, switch shops on or off, extend access, add-ons, AI usage and cost, confirm payments. Allowed emails come from `Platform__AdminEmails`. |

## 4. Features built (all merged and tested)

**Booking core:** public booking page, availability from working hours, buffers and closures, walk-ins, reschedule, cancel with cutoff, day view with clickable summary cards, no-show and complete actions, audit log, follow-ups and reminders (manual lists with ready messages).

**After a visit:** payment total (a Settings switch decides whether staff may type it), "Add another service" with a typed Amount, the "Other" service, follow-up (optional), medical record (clinics only), receipt-like lines the customer also sees.

**Record a sale by hand** (Settings switch, off by default): a service and payment entered without a booking; walk-in customer when there is no number.

**Promo:** Promo Creator (nine modes, headline, badge, discount up to 90 percent, "Part of the promo" per service), logo creator, regular and net prices everywhere.

**Promo messages to customers (only to customers who said yes):** consent box on the booking and walk-in forms (never pre-ticked), consent recorded or withdrawn in the customer's history (audited), a "Send a promo" page with a ready message per customer, buttons for text, email, WhatsApp, Viber, Telegram and the phone's share list, "mark as sent", and an **automatic promo email** worker (daily-limit aware, stops by itself, every email has an unsubscribe link at `/Unsubscribe/{token}`).

**AI receptionist** (paid add-on, Anthropic API behind `IAiModel`, default model `claude-haiku-5-5`): chat on the booking page that checks slots and books through the same rules as the form. Limits per shop (per day, per month, tokens) and a cost monitor at `/Platform/Ai`. The live API was never called in tests.

**Billing (manual, no gateway):** prices from settings (Pro 499 per month or 4,990 per year; AI 1,500 and Email 199 per month are placeholders). Owner page "Plan and billing" with GCash QR, "I paid" with the reference number, history and a printable confirmation (says it is not a BIR receipt). Platform owner confirms, rejects or records by hand; confirming extends the access or add-on and gives a receipt number `VF-year-number`. Trial of 30 days with everything included, then the back office locks until paid (the Billing page stays open).

**Platform tools:** shop list as summary lines with search, expiry dates and a heads-up when a client's access ends soon, subscription table per shop, payments, AI usage.

**Other:** in-app Help and guide (topic per feature, `HelpTopics.cs`), Privacy Notice and Terms pages, email confirmation and password reset, customer data export and erase (Data Privacy Act), reports (revenue, promo discounts given), PWA basics, QR code for the booking page.

## 5. Configuration (names only; values are in Render, never in git)

Required or important: `ConnectionStrings__Default`, `Database__MigrateOnStartup`, `App__PublicBaseUrl` (email links), `Platform__AdminEmails`, `Email__BrevoApiKey`, `Email__From`, `Email__FromName`, `Signup__RequireEmailConfirmation`, `Trial__Days`, `Trial__ContactUrl`, `Proxy__TrustForwardedHeaders`, `Legal__OperatorName`, `Legal__ContactEmail`, `Billing__SellerName`, `Billing__PayTo`, `Billing__Price`, `ANTHROPIC_API_KEY`.

Optional: `Turnstile__SiteKey` and `Turnstile__SecretKey` (captcha), `Pricing__ProMonthly`, `Pricing__ProYearly`, `Pricing__AiMonthly`, `Pricing__EmailMonthly`, `Billing__AiAddonPrice`, `Billing__EmailAddonPrice`, `AiReceptionist__InputUsdPerMillionTokens`, `AiReceptionist__OutputUsdPerMillionTokens`, `AiReceptionist__UsdToPhp`, `AiReceptionist__MonthlyConversations` (300), `AiReceptionist__MonthlyTokens` (default 4,000,000), `Email__DailyLimit` (300), `Email__FreeShopDailyMax` (30), `Notices__Enabled`.

## 6. How to work on it

- Start every session with `git pull --rebase origin main`. Commit small, push often. Never force-push `main`.
- Commands: `dotnet run --project tests/VeskaFlow.Core.SelfCheck`, `dotnet build VeskaFlow.sln`, and for the Web tests a disposable PostgreSQL database whose name ends in `_test` (`ConnectionStrings__Test=...`).
- Every change: tests, then a pull request merged to `main`. The user must press **Manual Deploy** in Render for a change to go live; migrations run at startup.
- Be honest about verification: say what was compiled, run and checked in a real browser, and what was not.

## 7. What is not verified yet (be honest about these)

- Nothing was checked on a real phone or in Safari (Chromium at phone width only).
- No real email was delivered through Brevo or Resend, and no real call was made to the Anthropic API. AI cost numbers use placeholder prices.
- A real GCash payment through the billing flow was never tried.
- WhatsApp, Viber and Telegram links were not opened in the real apps (Telegram's link form is the least certain).
- Privacy, Terms, the promo consent wording and the unsubscribe footer are drafts that need a lawyer (`docs/LEGAL_BRIEF.md`).
- The confirmation of payment is not an official BIR receipt; ask an accountant if one is needed.

## 8. Not built yet

Payment gateway (PayMongo or Xendit) and automatic renewal; payment method (cash or GCash) on each service payment and printable receipts for shops; a public pricing page; a permanent free plan after the trial; SMS and Messenger or Viber channels for the AI; scheduling a promo for later; Tagalog interface and guide; the JSON API (`/api/v1`) and the Flutter app; wait list, packages or memberships, inventory, staff commission.

## 9. Open decisions

- Real prices for the AI and Email add-ons once the AI cost is measured (`/Platform/Ai`).
- Whether to keep the lockout after the trial or offer a permanent Free plan.
- Whether to pay for an always-on Render instance (the free one sleeps; an uptime monitor currently keeps it awake).
- Which industries to support first and how each one should differ (see the industries note the owner is preparing).
