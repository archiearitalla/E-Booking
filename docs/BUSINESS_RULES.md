# VeskaFlow — Business Rules

Source of truth for how the booking system behaves. Code, API, database and every client (web, admin, mobile, widget) must follow this document. If a requirement conflicts with an invariant in section 4, raise it here first and change this file in the same pull request as the code.

- **Status:** Draft v0.1 — for owner review
- **Product:** Multi-tenant SaaS booking for small service businesses (barbershops and salons first)
- **Stack:** ASP.NET Core 8, PostgreSQL 16, EF Core (Npgsql)
- **Guiding principle: free first.** Build and pilot at zero cost. No paid service until there are paying clients to cover it (section 17).
- **Legend:** ✅ implemented and verified here · 🟡 written, not yet compiled or run · ⬜ planned · ❓ open decision (section 15)

---

## 1. Scope

**In scope for the MVP**

- Appointment bookings: a customer picks a service, an optional staff member, a date and a time.
- Pay on site. No online payment in the MVP.
- Many independent businesses ("tenants") on one deployment, each with its own booking page `/b/{slug}`.
- Business owner back office: bookings, services, staff, working hours, closures, settings, audit log.
- Free plan limited to 1 staff member.

**Out of scope for the MVP** (see roadmap, section 14): online payments and deposits, SMS/email reminders, rescheduling, multi-branch, waitlists, loyalty, events/classes, restaurant/table reservations, customer accounts.

## 2. Glossary

| Term | Meaning |
|---|---|
| Business (tenant) | One shop. Owns staff, services, customers, bookings. Identified publicly by `slug`. |
| Staff | A bookable resource: one person whose time can be reserved. Capacity is always 1. |
| Service | What is booked (Haircut). Has duration, optional buffer, price. |
| Working hours | Weekly open windows per staff member, in the business's local time. |
| Blackout | A closure (holiday, leave) that overrides working hours. Shop-wide or for one staff member. |
| Slot | A possible start time, derived on demand. Never stored. |
| Buffer | Cleanup time after a service that blocks the staff member but is not part of the appointment. |
| Manage token | Unguessable secret in the customer's booking link. The customer's only credential. |
| Actor | Who performed a change: `CUSTOMER`, `STAFF` or `SYSTEM`. |

## 3. Platforms and the shared-API principle

Every surface talks to **one** booking API that owns the invariants. Availability, double-booking protection, cancellation windows and authorization are never re-implemented in a client. Clients may *display* rules, but the server decides.

| Surface | Users | Responsibility | Status |
|---|---|---|---|
| Public booking page (server-rendered) | Customers | Browse services, pick time, confirm, manage/cancel via token | ✅ |
| Admin back office (server-rendered) | Owner (later manager/staff) | Calendar, status changes, configuration, audit | ✅ (OWNER only; walk-in form built) |
| JSON API `/api/v1` | Mobile apps, embeddable widget, integrations | Same operations as above over HTTP | ⬜ |
| Customer / staff mobile app (Flutter, one codebase for Android and iOS) | Customers, staff | Thin client of the JSON API | ⬜ |
| Embeddable widget | Shop's own website | Iframe or script around the public API | ⬜ |

**Design rules for the platforms**

1. The Razor pages and the JSON API both call the same application services (`BookingService`, `OnboardingService`). No business logic lives in pages or controllers.
2. The API is versioned (`/api/v1`). Breaking changes require `/v2`; mobile apps in the wild cannot be force-updated.
3. Business behavior is **configuration, not code**: slot size, lead time, horizon, cancellation cutoff, buffers, durations and hours are per-business data (section 6). This is what makes the system "dynamic" per tenant.
4. Clients never send prices, durations, tenant ids or customer ids as trusted values. The server derives them from the service, the slug and the token.
5. Errors use one shape across platforms (section 8.4).

### 3.1 Planned API surface (`/api/v1`) ⬜

Public, unauthenticated, rate limited:

| Method | Path | Purpose |
|---|---|---|
| GET | `/businesses/{slug}` | Business info, active services, active staff |
| GET | `/businesses/{slug}/availability?serviceId&staffId&date` | Free start times for one local date. `staffId` optional = any available |
| POST | `/businesses/{slug}/bookings` | Create a booking. Requires `Idempotency-Key` header |
| GET | `/bookings/{manageToken}` | View a booking |
| POST | `/bookings/{manageToken}/cancel` | Customer cancellation, subject to the cutoff |

Authenticated (owner/staff):

| Method | Path | Purpose |
|---|---|---|
| POST | `/auth/login` | Credentials for non-browser clients. ❓ token format (section 15) |
| GET | `/admin/bookings?from&to&staffId&status` | Calendar data |
| POST | `/admin/bookings` | Staff creates a booking for a walk-in or phone customer |
| POST | `/admin/bookings/{id}/transition` | Complete, no-show, cancel with reason |
| CRUD | `/admin/services`, `/admin/staff`, `/admin/blackouts` | Configuration |
| PUT | `/admin/staff/{id}/hours`, `/admin/settings` | Hours and business settings |

## 4. Non-negotiable invariants

These must hold at all times. Everything else is policy.

| # | Invariant | Enforced by | Verified |
|---|---|---|---|
| I1 | A staff member is never double-booked. | PostgreSQL exclusion constraint `no_double_booking` on `(staff_id, active_range)` | ✅ 40 simultaneous inserts of one slot: 1 wins, 39 rejected |
| I2 | Availability is derived, never stored. A slot is free only if inside working hours, outside blackouts, clear of active bookings plus buffers, after the lead time and inside the horizon. | `AvailabilityCalculator` (pure function), reused at confirmation | ✅ 32 unit checks |
| I3 | Status changes follow the lifecycle in section 5. Cancelling frees the slot in the same transaction. | `BookingStateMachine` + generated `active_range` column | ✅ |
| I4 | Instants are stored in UTC. The business's IANA time zone is stored alongside. No naive local times, no fixed offsets. | `timestamptz` columns, `location_time_zone`, `LocalTime` helpers | ✅ DST days verified (23h/25h) |
| I5 | Holds are temporary and self-expiring. | `hold_expires_at_utc` + expiry job | ⬜ not used in the MVP (no HELD flow yet) |
| I6 | Money and booking agree. A booking is confirmed only when its required payment state is satisfied. | Not applicable while pay-on-site | ⬜ |
| I7 | A tenant can never see or modify another tenant's data. | Every query filtered by `business_id`; composite foreign keys `(id, business_id)` in the database | ✅ cross-tenant FK rejected; 🟡 query filters |
| I8 | A customer can access only their own booking, via the manage token. | Lookup by token; unknown token returns not found | ✅ HTTP tests: unknown token, slug and URL return the identical 404 page |

## 5. Booking lifecycle

```
              ┌────────── expires (TTL) ─────────┐
              ▼                                   │
 (create) → HELD ──confirm──→ PENDING ──approve/pay──→ CONFIRMED ──attend──→ COMPLETED
              │                  │                      │
              │                  │                      ├── cancel ──→ CANCELLED
              └──── abandon ─────┴──→ CANCELLED         └── absent ──→ NO_SHOW
```

**MVP behavior:** pay-on-site and instant confirmation, so a booking is created directly as `CONFIRMED`. `HELD` and `PENDING` exist in the model and database for online payment later.

| Rule | Detail |
|---|---|
| Occupies the slot | `HELD`, `PENDING`, `CONFIRMED` |
| Does not occupy | `COMPLETED`, `CANCELLED`, `NO_SHOW` |
| Terminal | `COMPLETED`, `CANCELLED`, `NO_SHOW`. No way back. |
| Complete | Only from `CONFIRMED`, and only after the start time |
| No-show | Only from `CONFIRMED`, and only 15 minutes after the start (grace period) |
| Cancel | From `HELD`, `PENDING`, `CONFIRMED`. See section 7 |
| Every change | Writes an audit row (section 11) |
| Concurrent edits | Optimistic concurrency (`xmin`). The second writer is told the booking changed and must refresh. |

## 6. Availability rules

Configurable per business unless noted. Defaults apply to new businesses.

| Setting | Default | Allowed | Meaning |
|---|---|---|---|
| Slot size | 30 min | 5, 10, 15, 20, 30, 60 | Step between offered start times |
| Lead time | 60 min | ≥ 0 | Cannot book closer than this to now |
| Horizon | 30 days | 1–365 | Cannot book further ahead than this |
| Cancel cutoff | 2 h | ≥ 0 | Customers may cancel until this long before the start |
| Service duration | 30 min | 5–600 | Per service |
| Service buffer | 0 min | 0–240 | Per service. Blocks the staff member after the appointment |
| Time zone | `Asia/Manila` | IANA id | Per business |

**How a free slot is decided** (all must be true):

1. The start is inside one of the staff member's windows for that weekday, and `start + duration` fits before the window closes. The buffer may extend past closing time.
2. It does not overlap a blackout (shop-wide or this staff member). A blackout is either a one-off closure (a date range) or a **weekly closure**: a closed weekday that repeats every week, for the whole shop or one staff member, with an on/off switch. A weekly closure that is on closes that whole local day (walk-ins included); switched off it closes nothing but stays in the list. Closing a day never cancels bookings; the page says how many upcoming bookings fall on it. One page (Closures) lists both kinds together.
3. `[start, start + duration + buffer)` does not overlap any active booking's occupied range. Back-to-back is allowed: a range ending exactly when another starts is not an overlap.
4. `start ≥ now + lead time` and `start ≤ now + horizon`.
5. The staff member is active and offers the service.

Further rules:

- A day may have several windows (split shift for lunch). A window whose close is before its open crosses midnight (for example 18:00–02:00).
- Slots are generated in UTC from the window's local open time, so DST transitions never skip or duplicate a slot.
- **"Any available"** is not stored as null. At confirmation the server assigns a concrete staff member: among free eligible staff, the one with the fewest bookings that day, then by sort order.
- The server re-runs the availability check at confirmation. A page that was loaded earlier is never trusted.

## 7. Cancellation, no-show and rescheduling

| Actor | Cancel | Condition |
|---|---|---|
| Customer (token) | Allowed | Booking is active, appointment not started, and now ≤ start − cutoff |
| Staff / owner | Allowed | Any time while the booking is active. Reason recommended, recorded |
| System | Allowed | Hold expiry (future) |

- A refused customer cancellation shows the shop's cutoff and tells the customer to contact the shop.
- No-show: marked by staff after the grace period. No fee in the MVP (pay on site). The count per customer is a roadmap item.
- **Rescheduling** ⬜: will be atomic. The new slot is secured first, then the old one released, so the customer never loses both. Until then, cancel and rebook.
- Closing a shop day or changing hours **never silently cancels** existing bookings. The admin sees a warning with the number of affected bookings and handles them deliberately.

## 8. Booking creation

### 8.1 Flow

1. Customer chooses service, optional staff, date; the page shows free start times (derived, section 6).
2. Customer enters name, phone and optional email. The form carries a one-time idempotency key.
3. Server validates input, loads the service and eligible staff **for this business only**, re-checks availability, then attempts the insert.
4. If the database rejects the insert (exclusion constraint), the server tries the next eligible staff member (for "any available"). If none succeed the customer gets "that time was just taken, please choose another".
5. On success the booking is `CONFIRMED`, an audit row is written in the same transaction, and the customer is sent to their manage link.

### 8.2 Rules

- **Idempotency:** the same key from the same business creates at most one booking. A retry returns the existing booking (unique index on `(business_id, idempotency_key)`).
- **Price and name snapshot:** `price_snapshot` and `service_name_snapshot` are copied at booking time. Later price edits never change old bookings.
- **Customer identity:** customers are per business, matched by normalized phone number. A repeat booking updates the name and email on file.
- **Phone:** digits with an optional leading `+`, 7 to 15 digits.
- **Staff-created bookings** (walk-ins, phone-ins) skip the lead time, so a booking can start right now. They still must be inside working hours, outside blackouts and free of overlaps, and the database constraint still applies. "Right now" means the slot already in progress: a walk-in may start up to one slot length (the business's slot size) before the current time (decided by the owner). The actor is recorded as `STAFF` in the audit log. The phone number is optional for a staff-created booking: without one the customer is stored with a generated placeholder (prefix `walkin-`), is never shown or dialled, and is never matched with a later visit. A customer booking still needs a valid phone. The back-office page is `/Admin/WalkIn` (pick service and who, choose a free time today, name, optional phone); it books through the same service, so the database constraint and the working-hours rules apply.
- **Input limits:** name 1–80 chars, email ≤ 120 chars and valid format, notes none in MVP.

### 8.3 Race handling

Two customers confirming the last slot at the same moment is expected, not exceptional. Exactly one wins in the database; the loser receives a normal "slot taken" response (HTTP 409 in the API), never a server error.

### 8.4 API error shape ⬜

`application/problem+json` with a stable machine-readable `code`:

| Code | HTTP | Meaning |
|---|---|---|
| `validation_failed` | 422 | Bad or missing input; field errors included |
| `slot_taken` | 409 | The chosen time is no longer available |
| `policy_violation` | 409 | For example cancellation after the cutoff |
| `stale` | 409 | The booking changed since it was loaded |
| `not_found` | 404 | Unknown slug or token, or not your tenant (indistinguishable on purpose) |
| `rate_limited` | 429 | Too many requests |
| `unauthorized` / `forbidden` | 401 / 403 | Not signed in / not allowed |

### 8.5 Message links instead of an SMS gateway

No SMS gateway is used (it is the only provider cost with no free tier). Messages are sent by a person from their own messaging app, and the system only prepares the link:

- **Customer to shop (SMS):** the confirmation page has a "Message the shop" button that opens the customer's messaging app with the shop's number and a prefilled text (service, date and time, booking reference, manage link).
- **Customer to shop (Messenger):** if the shop sets its Facebook Page username in settings, the confirmation page also shows "Message us on Messenger", linking to `https://m.me/<page-username>`. Meta's documentation describes `m.me` links for Pages only and documents no way to prefill text, so the button first **copies the booking message to the clipboard** and the customer pastes it into the chat. Once the customer has messaged the Page, the shop continues the conversation from the Page inbox.
- **Shop to customer:** each booking in the back office has a "Message customer" button that opens the staff member's own messaging app with the customer's number and a prefilled reminder or confirmation text. A "Copy message" button is the fallback when the device cannot open a message app (most desktop browsers).
- **Messenger limits:** a phone number cannot open a Messenger chat, so the shop cannot start a Messenger conversation with a customer from our data alone. An optional "Messenger link" field on the booking form is deferred ❓ (opening a person's chat through `m.me/<username>` is not covered by Meta's documentation, so it needs testing first).
- The text is prefilled but always editable by the sender. Templates are fixed in English for the MVP.
- Links are built with the `sms:` URI scheme. The exact form that works on both Android and iOS must be tested on real devices ❓.
- No delivery tracking, no automatic sending, no cost. Automatic reminders, if wanted, use free email (section 17).
- Needs new business settings, `contact_phone` and an optional `messenger_page` (the Page username, not a full URL), added by an additive migration `003_*` ⬜ (`002_brand_color.sql` already exists).
- Message bodies must be URL-encoded, and phone numbers are shown only to signed-in staff of that business, never on public pages.

### 8.6 Limits on the open booking page ✅

The public booking page needs no account, so anyone can use it. One person must not be able to double-book or hoard time, and a bot must not be able to fill the day.

- **Same person.** A person is recognised by phone number, in one canonical form: `0917 123 4567`, `+63 917 123 4567`, `63917 123 4567` and `917 123 4567` are all `09171234567` (`PhoneNumber.Normalize`). Other numbers keep their digits.
- **No overlapping bookings.** A customer booking is refused when the same person already has an active booking (`HELD`, `PENDING` or `CONFIRMED`) that overlaps the requested time in this business, even with a different staff member or service.
- **Cap on upcoming bookings.** A person may hold at most `Booking:MaxActivePerCustomer` (default 2) active bookings that have not ended. `0` turns the cap off. Cancelling one frees a place.
- Both refusals appear as an error under the phone field, not as a "slot taken" message. They are checked inside the booking transaction under a per-customer lock, so parallel requests cannot slip past.
- **Staff are exempt** (walk-ins and phone-ins), and a retry with the same idempotency key still returns the same booking.
- **Bots.** The confirm form carries a hidden field that people never see; if it is filled, nothing is created and the visitor is sent back to the time list. Booking posts are also limited per client IP (`RateLimits:BookingPerMinute`, default 6).
- **Known limit.** Someone who uses a different phone number for each booking is not caught by these rules; verifying a number needs SMS, which has no free option (section 17). The rate limit, the honeypot and the staff's ability to cancel are the remaining defences.

### 8.7 QR code for the booking link ✅

- Settings shows a QR code for the shop's own booking page (`/b/{slug}`), with a download as an SVG file and a Print button whose print layout shows only the code. The signed-in business id decides the shop; a request cannot ask for another shop's code.
- The address in the code is `App:PublicBaseUrl` when set (production: `https://flow.veskacode.com`), otherwise the address the page was opened on. The code is generated on the server with no external service and the same address always gives the same image.


## 9. Tenancy, roles and authorization

- All back-office data access is scoped by the signed-in user's `business_id`. It is taken from the authenticated principal, never from the request.
- Customers have no accounts in the MVP. The manage token authorizes exactly one booking.
- Role tags are displayed as badges, but authorization is always enforced server-side.

| Role | MVP status | Can |
|---|---|---|
| OWNER | ✅ | Everything for their business |
| MANAGER | ⬜ | Everything except billing/plan and deleting the business |
| STAFF | ⬜ | Own schedule, mark complete/no-show, create walk-ins, block own time |

- Passwords: PBKDF2-SHA256, 600,000 iterations, per-password salt, constant-time compare. Unknown emails take the same code path as wrong passwords.
- Sign-in and booking endpoints are rate limited per client IP.

## 10. Pricing and payments

- MVP: pay on site. Prices are shown in pesos (₱); the currency is not configurable yet. A booking keeps the price it was quoted (after any promo discount, 6.y18); the payment total entered when the visit is done records what was actually paid (6.y19).
- Online payment ⬜ (PayMongo or Xendit): required deposit moves a booking through `PENDING`; the slot is held for a TTL; payment failure or timeout cancels the hold. Payment and booking endpoints must be idempotent. Captured money must never exist without a held slot (I6).

## 11. Audit

Every booking status change and creation writes: business, booking, actor type, actor id, actor label, action, from status, to status, reason, UTC time. The owner can view the latest entries. Audit rows are never edited or deleted by the application.

## 12. Plans and limits

| Plan | Staff | Notes |
|---|---|---|
| FREE | 1 | Enforced server-side when adding staff. During the pilot every feature is free |
| PRO | ❓ | Price and limits to be decided. Billing is not built |

Pricing hypotheses (₱299–₱999 per month for PRO, after the free pilot) are assumptions to test with the first 3–5 pilot shops, not commitments.

## 13. Security and privacy

- CSRF protection on every form POST; cookies are `HttpOnly`, `SameSite=Lax`, secure over HTTPS.
- Parameterized queries only (EF Core). Output encoded by Razor.
- Security headers: CSP, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`.
- Public availability responses contain times only. No customer names or phone numbers are exposed to other customers.
- Secrets (connection strings) come from environment variables or a secret store, never from the repository.
- Phone and email are personal data under the Philippine Data Privacy Act: collect only what is needed, state the purpose on the booking page, support deletion on request. ⬜ privacy notice and deletion tooling.
- Retention: customer details are kept 24 months after the last booking, then anonymized (name and phone cleared, bookings kept for statistics). A customer can ask for deletion at any time. ⬜ job and tooling.
- Database changes are additive and applied through numbered migrations. No `DROP`, `TRUNCATE` or destructive `ALTER` on existing tables without a reviewed plan and a verified backup.

## 14. Roadmap (not in the MVP)

1. JSON API `/api/v1` and Flutter mobile app
2. Free email and push reminders (Resend, FCM). A paid SMS gateway only if a paying shop asks for it
3. Rescheduling (atomic)
4. Staff-created walk-in bookings; manager and staff roles
5. Online deposit via PayMongo/Xendit with holds and an expiry job
6. Subscription billing for the PRO plan
7. Hold-expiry job and payment reconciliation
8. Customer no-show record and optional fee
9. Multi-branch, other booking types (rooms, tables, classes)
10. Tagalog and English UI toggle

## 15. Open decisions for the owner

1. ~~Language~~ **Decided: English only for the MVP.** All user-facing text lives in one place so Tagalog can be added later without touching logic.
2. ~~Default cancel cutoff~~ **Decided: 2 hours**, configurable per business (section 6).
3. ~~Walk-ins~~ **Decided:** staff-created bookings skip the lead time but still respect working hours, blackouts and overlaps (section 8.2).
4. ~~Customer identity~~ **Decided: phone number** (section 8.2). Email is optional.
5. ~~Reminders~~ **Decided: no SMS gateway.** Messages are sent manually through a prefilled link that opens the sender's own messaging app (section 8.5). Free email and push remain optional (section 17).
6. ~~Mobile auth~~ **Decided (recommended default, no objection raised):** short-lived JWT access token (about 15 minutes) plus a refresh token kept in the phone's secure storage. Web admin keeps its cookie.
7. **Plan limits, working default (not yet confirmed):** all features are free for pilot shops. The FREE plan keeps the 1-staff limit in code so a PRO plan can be defined after the pilot.
8. ~~Data retention~~ **Decided (recommended default, no objection raised):** keep customer details 24 months after the last booking, then anonymize. Deletion on request always applies.
9. ~~Mobile client~~ **Decided: Flutter** (cross-platform, Android and iOS from one codebase).

## 16. Verification status

| Area | Status |
|---|---|
| Availability, DST, buffers, blackouts, lead time, horizon | ✅ 55 self-checks pass (`tests/VeskaFlow.Core.SelfCheck`) |
| State machine, cancellation and no-show policy, passwords, slugs | ✅ same run |
| Database invariants (race, overlaps, buffers, lifecycle, cross-tenant, constraints) | ✅ 20 checks pass against PostgreSQL 16 (`tests/db/double_booking.sh`) |
| EF mapping and SQL migrator (`Data/*`) | ✅ compiled; `tests/VeskaFlow.Web.ModelCheck` passes against PostgreSQL 16 (all tables, `xmin`, booking round trip, migrator idempotent) |
| `BookingService` (availability, create, idempotency, transitions, audit) | ✅ 21 integration tests against PostgreSQL 16, including 50 parallel requests for one slot |
| Registration, sign-in/out, cookie claims, `/Admin` protection, sign-in rate limit, antiforgery | ✅ 20 tests over services and HTTP against PostgreSQL 16 (`Secure` cookie flag and real-browser rendering not verified) |
| Public booking pages (`/b/{slug}`, confirm, `/booking/{token}`) | ✅ 12 HTTP tests plus a real-browser run at phone width; not verified: stopwatch timing, Safari or a real phone, screen reader |
| Admin back office (day view, services, staff and hours, closures, settings, audit, About) | ✅ 30 tests incl. tenant isolation on every endpoint, plan limit, "never silently cancels" warnings; not verified: screen reader, keyboard-only walkthrough of every form, Safari or real phones |
| AI receptionist (6.y17): add-on switch, limits, tools on the real booking rules, tenant isolation, chat endpoint, SDK adapter | 🟡 21 tests (17 service and HTTP with a scripted model, 4 for the SDK adapter against a fake Messages API) on PostgreSQL 16.15; a real-browser run at 375x812 against the real app with a fake Messages API; migration 021 applied to a database that already had a shop. **Not verified: any call to the live Anthropic API (no key was available), the quality of real Tagalog conversations, the real cost per conversation (estimates only)** |
| Message links | ⬜ not written yet (Step 6 of the build plan) |
| Integration test of the full booking flow against the real app | ⬜ |
| Load test of the booking race through the application | ⬜ |

## 17. Providers: free-tier plan (checked 2026-10-17)

Principle: start at ₱0 and keep every provider behind a configuration switch, so moving to a paid plan or another vendor never touches business logic. The app talks to `IEmailSender` and `ISmsSender` interfaces ⬜, and the database and secrets come from environment variables.

| Need | Free choice | Known limits (verify before relying on them) | Risk |
|---|---|---|---|
| Source code | GitHub private repository | n/a | none |
| Database | **Decided: Neon Postgres, Free plan, PostgreSQL 16** | 1 GB storage, 100 compute-hours per project per month, compute suspends after 5 minutes idle and wakes automatically, no card. `btree_gist` is listed as supported (version 1.7 on PostgreSQL 16) | First query after idle is slower. Hitting a limit suspends compute or blocks writes until upgrade. Free-plan availability of `btree_gist` is not stated in the docs, so it is confirmed by the first migration (see below) |
| Web hosting | Render free web service (Docker image) or Google Cloud Run always-free | Render: 512 MB RAM, sleeps after 15 idle minutes (about 1 minute to wake), 750 instance-hours per month. Cloud Run: 2M requests per month, card required | Cold start on the first customer. Not for a paying shop without a keep-warm check or a paid plan |
| Email (confirmations) | Resend free | 3,000 emails per month, 100 per day, 1 verified domain | A busy shop can reach the daily cap. Sending needs a domain you own |
| Push (Flutter app) | Firebase Cloud Messaging | No per-message charge, as far as I know ❓ | Needs the mobile app installed |
| SMS reminders | **Not used** | No free SMS gateway exists (Semaphore lists about ₱0.56 per text excluding VAT) | Replaced by message links that open the user's own messaging app (section 8.5) |
| Payments | Not needed (pay on site) | n/a | n/a |

**Rules that follow from this**

- The free tier must not weaken any invariant. If a limit is hit, the system degrades by refusing new bookings clearly, never by double-booking.
- Free hosting is for development and pilots with friendly shops. Move to a paid plan before charging customers.
- Reminders are manual message links (section 8.5). Automatic email and push are optional and free. SMS gateway is out unless a paying shop funds it.

### 17.1 When to start paying (upgrade triggers)

Rule: **no paid provider before there is paying revenue to cover it.** Each upgrade is triggered by a concrete event, not by a feeling.

| Trigger | Upgrade |
|---|---|
| First paying shop goes live | Web hosting that does not sleep when idle (a customer must never wait about a minute for the booking page) |
| Database reaches about 70% of the free storage or compute-hours | Paid database plan, which also adds restore and backups. Until then, take a regular `pg_dump` backup yourself |
| Email daily cap (100 per day) is hit | Paid email plan |
| A paying shop asks for automatic SMS | Add an SMS gateway, billed to that shop. Until then messages use the manual links in section 8.5 |
| Mobile app ready for release | Store developer accounts (Google Play and Apple have their own fees, check the current amount) |

Free-tier limits change over time, so recheck each provider's pricing page before relying on a number in this section.

### 17.2 Cloudflare

- **Not the app host for now.** Cloudflare Containers (the way to run an ASP.NET Core Docker image there) is not on the Workers Free plan; it needs Workers Paid. Workers themselves run JavaScript/TypeScript/WebAssembly, not ASP.NET Core.
- **Good as the front door** (verify each against current docs before relying on it): DNS, TLS and CDN in front of the real host, bot protection on the booking form, hosting the Flutter web build or a landing page as static files.
- **Alternative later:** rewrite the API as Workers plus Hyperdrive to Postgres. Workers Free allows 100,000 requests per day and 10 ms CPU per request, and Hyperdrive Free allows 100,000 queries per day. It would not sleep like Render, but it abandons the C# code and tests, so it is only worth reconsidering if the free hosting cold start becomes a real problem.

### 17.3 Database decision and its safety check

- **Chosen:** Neon Free, PostgreSQL 16, because the double-booking guarantee (invariant I1) needs `btree_gist` and Neon lists it, and the project does not pause after a week of inactivity.
- **Fallback:** Supabase Free also lists `btree_gist`, but the project pauses after one week of inactivity and has no automatic backups, so it is only a fallback.
- **Gate before going live:** on the real Neon database, `CREATE EXTENSION btree_gist` must succeed and `tests/db/double_booking.sh` must pass against a separate Neon branch or database (the script wipes its schema, so never point it at live data). If either fails, do not go live; switch database first.
- **Backups:** the free plan has a very short history window and no scheduled snapshots. Take a regular `pg_dump` and store it outside Neon.

## 18. Industries and the trial 🟡 (simple version written and compiled; tests not yet run)

Purpose: let a business try the system live for 30 days so it can see how it works. Kept deliberately simple.

### 18.1 Industry

- A business has an `industry` chosen at sign-up: `barbershop`, `salon`, `clinic`, `spa` (spa and massage), `nails` (nail and lash studio), `pet` (pet grooming and vet), `fitness` (gym, trainer or yoga), `tattoo` (tattoo and piercing), `auto` (car wash and auto care), `tutor` (tutoring and lessons) or `photo` (photo studio).
- The industry only chooses the starter service (for example Haircut, Massage, Manicure, Grooming), the word for the person being booked (Barber, Stylist, Doctor, Therapist, Technician, Groomer or vet, Trainer, Artist, Teacher, Photographer) and the list of suggested services. It never changes the booking rules in sections 5 to 8 or the invariants in section 4. Everything can be edited afterwards.
- Admin → Services has "Add suggested services": it adds the usual services for the industry that the shop does not have yet (matched by name, ignoring case; prices start at 0), links them to all current staff, and never changes or removes existing ones. Running it twice adds nothing the second time.

### 6.x Closure times (added with weekly closures)

- A one-off closure can have a start and an end time (both or neither): it then runs from the first day at the start time to the last day at the end time (business time zone). Without times it closes whole days.
- A repeating closure can be for one weekday or every day, and for the whole day or a time window such as 12:00 to 13:00 (a lunch break). It can be switched off and on. Times are local wall-clock times in the business time zone.
- Existing bookings are never cancelled; the owner is told how many fall inside.
- Opening hours are set once in Settings as ONE range (opens, closes, optional break) that applies to all 7 days (`business_hour`, same shape as `working_hour`; a break is two windows on one day). Days off, such as every Sunday, are set as closures. Saving copies the range to every staff member's weekly hours, replacing them, and new staff start with it. A person's own hours can still be changed on the Staff page afterwards; later Settings saves overwrite those again.
- The shop's opening hours are a hard limit: nothing outside them can be booked or shown as free, for customers and for walk-ins, even if a staff member's own hours are wider. A time closure outside them is refused because it would close nothing. Shops that never set opening hours (no `business_hour` rows) are not limited this way. Times are picked with hour, minute and AM/PM dropdowns (stored as 24-hour `HH:mm`).

### 6.x2 Half-day closures

- A time closure needs no both-times entry: only **From** closes from then until the end of the day; only **Until** closes from the start of the day until then; both close that part of the day. **Until 12:00 AM** after a later From means the end of the day. The list shows "end of day" and "start of day". Whole-day closures leave both empty. An end that is not after the start is refused.

### 6.y Follow-up visits

- A service may carry "suggested follow-up after N days" (1 to 730, empty = not applicable). It only suggests a date.
- Staff can set a follow-up date (and a note up to 200 characters) on a **completed** booking, in Bookings under Done. The date may not be before the visit or more than 2 years after it. Removing it clears it. Other statuses cannot have one.
- Admin → Follow-ups lists follow-ups not yet done, soonest first, with Overdue and Today badges. Each has a ready-made message (customer first name, shop, service, date, booking link). Staff copy it or send it as a text from their own phone (`sms:` link). The system never sends anything by itself. "Mark done" takes it off the list. Follow-ups that are overdue or due within the next 3 days are announced to staff: a strip at the top of every back-office page and the header bell (its badge counts them). All of it is scoped to the signed-in business.
- Message tools (under a completed booking in Done, and on each follow-up): a ready-made message (thank-you after the visit, reminder for a follow-up) with Copy, Send as text (`sms:`, customer's number filled in), Send by email (`mailto:`, only when the customer gave an email), Share to another app (the phone's share sheet: Messenger, Viber, WhatsApp and so on; shown only where the browser supports it) and Save contact (a `.vcf` contact card, so staff can find the customer in their phone contacts). The system sends nothing. Walk-ins without a number have no text or contact options. The contact card is scoped to the signed-in business; another business's booking is "not found".
- Schema: `service.follow_up_days`, `booking.follow_up_on`, `follow_up_note`, `follow_up_done` (migration 006, additive).

### 6.v Privacy (Data Privacy Act of 2012, RA 10173)

- Roles: the shop is the personal information controller of its customers' data and the platform operator is its processor; for the owner's own sign-up data the operator is the controller. The roles, what is kept, why, the service providers (Render, Neon, Brevo, Cloudflare), cookies, retention, security and a customer's rights are written in plain language on `/Privacy`; the arrangement, including the processor terms, on `/Terms`. Both are linked in every page footer. They carry a version (`LegalInfo.Version`); change it when the text changes in a way owners should agree to again. They are plain descriptions of how the system works, **not legal advice**, and should be reviewed by a lawyer before a clinic relies on them. `Legal:OperatorName` and `Legal:ContactEmail` fill in who runs it and the contact.
- Sign-up requires ticking agreement to both; the time and version are stored on the shop (`business.terms_accepted_at_utc`, `terms_version`; migration 011).
- A customer's right to access and erasure is carried out by the shop on the customer's history page: **Export** (a JSON file of the customer, their bookings with notes and follow-ups, and the audit trail of those bookings) and **Delete** (the customer, their bookings, records, follow-ups and their audit rows, in one transaction; the customer's name must be typed, and a browser confirm window asks first). Both are scoped to the signed-in business; another shop's customer is "not found". Chats with the AI assistant in which the customer typed their mobile number are part of both (6.y17).
- Visit notes (a clinic's medical record) are visible only to signed-in staff of that shop, are never shown to the customer and never copied into a message or the audit log.

### 6.w The "Other" service

- Every shop has a service named "Other" (`service.is_other`, one per shop, linked to every staff member; migration 010 added it to existing shops and onboarding adds it to new ones). It is listed last, for a visit whose service is not in the shop's list, on the online booking page and in walk-in.
- Choosing it requires a short description (1 to 60 characters, typed by the customer or by staff for a walk-in). The booking keeps it as its service name ("Other: <text>"), so the day view, the customer's page, history and messages show what was asked for. Online booking shows no price for it ("the shop will tell you the price"); the owner sets how long it takes (default 30 minutes) and can switch it off like any service. A description sent with an ordinary service is ignored.

### 6.y2 Appointment reminders

A confirmed booking that starts within the shop's reminder hours (Settings, 1 to 168, default 24) and has not been marked reminded is listed on the Reminders page, in the header bell and in a strip. Each row has a ready message (first name, service, time, the customer's own manage link) sent by staff by text, email, share or copy; the system sends nothing. Staff mark it reminded after confirming. Cancelled, past and started bookings are never listed. Every query is scoped by the shop.

### 6.y3 Customer list

The Customers page lists the shop's customers with completed visits, no-shows, total spent (sum of completed bookings' prices) and last visit; search by name, phone or email; CSV download of the same list (formula-looking cells are defused). Scoped by the shop.

### 6.y4 Reports

The Reports page shows, for this month, last month, the last 30 days or this year (in the shop's time zone): bookings by outcome, revenue (completed bookings only, at the price they were booked at), and completed visits and revenue by service and by staff member. Scoped by the shop.

### 6.y5 Moving a booking

Staff can move a confirmed booking that has not started to another free time with the same staff member and the same service length (the booking's own old slot counts as free, so a small shift works). The time is re-checked on the server, the database constraint stays the final guard, a taken slot is a normal message, the move is written to the audit log (RESCHEDULED, old to new time) and the booking is due a new reminder. The customer keeps the same link; staff tell them the new time. Customers cannot move bookings themselves.

### 6.y6 Extending a shop's access

The platform owner can add 7, 30, 90 or 365 days to a shop's access end date on the Platform page (after the client paid, or as a favour). The days count from the later of today and the current end date. Shops with no end date are left alone. There is no payment processing: the platform owner records payment by extending.

### 6.y7 Team roles

Three roles sign in to a shop's back office. STAFF run the day: bookings, walk-ins, reminders, follow-ups, visit records, moving a booking. MANAGER do everything STAFF do plus services, staff, closures, customers, reports and the audit log. OWNER also has Settings, the Team page and customer data export and erase. The role and the active flag are read from the database on every request, so switching someone off or changing a role works at once; a page a person may not open is "not found" for them and its menu entry is hidden. The owner adds a MANAGER or STAFF member with a name, email and a password the owner hands over (the owner vouches for the address, so no confirmation email); the owner can switch them off and set a new password. The owner cannot be switched off from the Team page, there is exactly one OWNER per shop, and a shop can have at most 20 people. One email is one account across the whole system.

### 6.y8 Shop logo

The owner can upload one logo (PNG, JPEG or WebP, at most 256 KB) in Settings and remove it. It is stored in the database (the host disk is not kept between deploys), its type is taken from the file's first bytes (SVG and anything else is refused), it is served publicly at /logo/{slug} with an ETag, and it shows at the top of the online booking page. Reports also accept exact From and To dates (swapped dates are ordered, at most 366 days).

**Logo Creator.** In the Logo card of Settings, an owner without a logo can make one: letters or a short name (up to 12 characters; the shop's initials are the default), a shape (circle, rounded square, square), colours (the six shop colours plus green, black and gold, gold and black; the default is the shop's own colour), an icon (none, scissors, star, heart, sun, drop, spark, smile, award) and a letter style (plain or classic), with a live preview. It is a closed set (`LogoStyles`), every colour pair keeps at least 4.5 : 1 contrast between the letters and the background, and typed text is escaped, never drawn as markup. The picture is drawn in the browser, turned into a 512 x 512 PNG and sent through the ordinary logo upload form, so the server checks it exactly like a picture chosen from the phone (type from the first bytes, at most 256 KB); nothing new is accepted by the server. It is hidden until its script runs, so without JavaScript the upload above is the way. The booking page and the Settings preview show a logo in its own shape (a wide banner is shown wide, a square one stays small).


### 6.y9 Forgot and change password

On the sign-in page, "Forgot your password?" asks for the email and sends a link that works for one hour and once. The page gives the same answer for every address, so it cannot reveal who has an account; only an active person in an enabled shop gets a link, and a second request within a minute sends nothing new. Opening the link and choosing a new password (8 to 128 characters) also counts as confirming the email. Only the SHA-256 of the token is stored. Without an email service the page says so. Anyone signed in can change their own password under My account by giving the current one. Existing sessions stay signed in until they end.

### 6.y10 Sign-up bot check

When Turnstile:SiteKey and Turnstile:SecretKey are both configured, sign-up and forgot password require a passed Cloudflare Turnstile check, verified on the server; a missing, failed or unreachable check refuses the request. The content policy allows challenges.cloudflare.com only while the keys are set. With no keys there is no check and nothing changes.

### 6.y11 Backup reminder

The Platform page shows a Database backup card: a link to the nightly backups, the date the platform owner last recorded saving a copy on their own drive (kept in platform_setting, migration 015), and a warning when none was recorded or 30 days have passed. Only the platform owner sees it or can press "I saved a copy".

### 6.y12 Day numbers and installing on a phone

The Bookings page shows the chosen day at a glance: still to do (confirmed), done, earned (completed bookings' prices), no-shows and cancelled. The back office can be installed on a phone as an app (web app manifest, icons, a service worker that caches nothing, so every page is fresh): Android Chrome "Install app", iPhone Safari "Add to Home Screen". Only back office pages offer it; the customers' booking page does not. It is the same website, not a store app.

### 6.y13 Renewing and reminders

A shop's access has an end date. The owner is emailed 7, 3 and 1 days before it (once each, only with an email service, only for an enabled shop with a confirmed owner, never after the end date; an extension starts the reminders over). The trial-ended page and About show the price and where to pay from settings (Billing:Price, Billing:PayTo, Billing:Note), never from code. Payment is manual for now (GCash): the platform owner extends access on the Platform page.

### 6.y14 A staff login tied to one staff member

A STAFF login can be tied to one bookable staff member (for example a barber) on the Team page. It then sees only that person's bookings, reminders, follow-ups and customers (customers that person has served, with only that person's visits), cannot open, change or move another staff member's booking (it is "not found"), and the walk-in page books for that person only. The alerts count only their work. A STAFF login with no tie sees the whole shop, as before. Managers and the owner always see the whole shop and cannot be tied. The tie is checked against the database on every request. Only the owner can change it.

### 6.y15 Automatic customer emails

When a customer gives an email at booking and the shop has the switch on (default on), they get a confirmation email, and a reminder email once the appointment is inside the shop's reminder window (the booking must be at least 3 hours old, the appointment more than 30 minutes away); an emailed reminder takes the booking off the manual Reminders list. Customers without an email, and everything after a limit is reached, stay on the manual list. Every email sent is counted per UTC day: customer emails stop at the daily limit minus an owner reserve, shops without the paid add-on stop earlier (their own daily share and a free pool that leaves a reserve for add-on shops), and the system's own emails to owners are never blocked by customer emails. The paid add-on is switched on by the platform owner on the Platform page after payment (manual, GCash), for 30, 90 or 365 days. The email names the shop, the service, the staff member and the time, and carries the customer's own manage link; it says not to reply.

### 6.y16 GCash QR code

The platform owner can upload the GCash QR code (a screenshot of My QR, PNG, JPEG or WebP, up to 512 KB, recognised by its first bytes) on the Platform page and remove it. Signed-in shop owners see it on About, in Settings (more emails) and on the trial-ended page; when a QR is uploaded it replaces the written Price and Pay to lines (they come back if the QR is removed); tapping it (or Save QR) downloads it and, on Android, opens the GCash app a moment later, with an Open GCash button (the QR is not scanned from the same screen; the client uses Pay QR, Upload QR). It is served only to signed-in people. The system does not generate a GCash QR itself, because only the GCash app produces a code its app accepts.

### 6.y17 AI receptionist (paid add-on) 🟡

A chat on the shop's public booking page where a customer asks about services, prices and free times and books, in English, Tagalog or a mix. It is a **paid add-on, off by default**: it exists for a shop only while `business.ai_addon_until_utc` is in the future and the server has a model key. The platform owner switches it on from the Platform page after the client paid (manual, like the email add-on), for 30, 90 or 365 days counted from the later of today and the current end. Without the add-on or a key the chat is not shown and its address answers with the same not-found as an unknown shop; no model is called and nothing is counted.

- **Same rules as the form.** The model can only call two tools, both our own code: `check_slots` (free times from `BookingService.GetFreeStartsAsync`, any available staff) and `create_booking` (`BookingService.CreateAsync`: availability is checked again, the exclusion constraint is the last word, the open-page limits of 8.6 and the trial-lapse rule apply, a lost race is a normal "slot taken" answer given back to the model). It cannot cancel or move a booking; the customer is given the manage link. Prices and durations come from the services list in the prompt, never from the model's memory. A repeated tool call in one customer message makes one booking. The audit row says `CUSTOMER` with the label `<name> (chat assistant)`.
- **Tenancy.** The shop comes from the booking address. The model never sees or sends a business id; a service id of another shop is refused, and another shop's chat id, an unknown chat id and a shop without the add-on all give the same not-found.
- **Cost control.** The model is `AiReceptionist:Model` (default `claude-haiku-5-5`, the cheapest) at its lowest effort. Each shop has `AiReceptionist:MonthlyConversations` conversations per UTC month (default 300), counted in one atomic statement when a conversation starts, so parallel starts cannot pass it; running conversations continue. One conversation holds at most 20 customer messages of at most 500 characters, one message costs at most 6 model calls, and chat posts are limited per client IP (`RateLimits:ChatPerMinute`, default 12). Tokens used are added to `ai_usage` per shop and month; the shop sees conversations used on its Settings page.
- **Data.** `ai_chat` keeps the words of a conversation as plain text (what the customer typed, including the name and phone they gave, and what the assistant said) and nothing else. A shop's conversations older than 30 days are deleted when a new conversation of that shop starts. The customer's own manage link is added to the reply by the server, word for word, and is never sent to the AI service. The chat tells customers it is an automated assistant and links to the Privacy Notice. A chat has no link to a customer, so the customer's export and delete (6.v) find it by the mobile number typed in it (last 10 digits, any format); a chat in which the customer never typed their number cannot be found that way, and a walk-in placeholder number matches nothing. The Privacy Notice and Terms describe the assistant, the AI provider as a service provider and the 30 days (version 2026-10-30). ⬜ They are plain descriptions, not legal advice, and still need a lawyer's review (see `LEGAL_BRIEF.md`) before the add-on is switched on for a real shop.
- **Failure.** A model error, a refusal or a model that keeps calling tools ends with a short friendly answer pointing to the booking form, which is always on the page. The failed message is not kept in the conversation.
- **Secrets and settings.** The key is `Anthropic:ApiKey` or the `ANTHROPIC_API_KEY` environment variable, never in git. `Anthropic:BaseUrl` is only for a gateway or a test. The price shown to clients is `Billing:AiAddonPrice`.
- **Limits per shop, and the cost view.** The platform owner can give each shop its own limits on **/Platform/Ai**: conversations per UTC day, conversations per UTC month and tokens per UTC month (`business.ai_daily_limit`, `ai_monthly_limit`, `ai_token_limit`, migration 028). Blank uses the default from settings (`AiReceptionist:MonthlyConversations` default 300; `DailyConversations` and `MonthlyTokens` default 0 = no limit); a typed 0 stops new conversations for that limit. The day and month are counted in one transaction, so a refusal by one limit counts nothing; the token limit is checked when a conversation starts (a running conversation can finish). The page lists every shop that has the add-on or has used it, one line each, biggest cost first, with the month's conversations, tokens and an **estimated cost** (PHP and USD) from `AiReceptionist:InputUsdPerMillionTokens`, `OutputUsdPerMillionTokens` and `UsdToPhp` (defaults 1, 5 and 58 are placeholders: set the real prices; the real bill is on the provider's console). It can be searched by shop, address or owner email and moved month by month. Only the platform owner sees it (not found for anyone else).
- **Not built:** Messenger and Viber channels (Meta App Review and Viber costs first), SMS, cancelling or moving a booking by chat, choosing a staff member in the chat, a limit that cuts a conversation off in the middle.

### 6.y18 Promo Creator (promo banner)

The owner makes a promo that shows as a banner at the top of the shop's public booking page (below the shop's name card, above the booking form), on the owner-only page `/Admin/Promo` ("Promo Creator" in the menu). No design skills or picture are needed: the owner picks a **mode**, writes the offer, and the banner takes on the mode's look. A live preview on the page changes as the owner types and picks; nothing is saved until the form is sent, and the server checks everything again.

- **Modes** (a closed list, like the shop colours; the page styles each one, so nothing typed reaches the styles): Plain (the shop's own accent), Halloween, Christmas, New Year, Valentine's, Summer, Back to school, Payday sale, Rainy season. Each has its own colours (chosen so the text keeps good contrast on every part of the gradient), its own decoration (an icon, drawn as decoration only), and its own small line above the headline (for example "Halloween special"). Stored in `business_banner.theme` and checked by the database.
- **Words.** A **headline** (1 to 100 characters, required), an optional **badge** (up to 16 characters, shown big, for example "20% OFF"), and an optional second line of **details** (up to 140 characters). All are shown as text, never as markup. Empty parts are not shown at all.
- **Picture (optional).** PNG, JPEG or WebP, up to 512 KB, recognised by its first bytes (never by the name or the type the browser claims; SVG is refused), kept in the database (`business_banner`, migrations 022 and 023). It sits at the top of the card. Saving without a new picture keeps the old one; "Remove the picture and keep the words" drops it. A banner can be words only.
- **Last day.** A date in the shop's own time zone, today up to one year ahead. The banner shows through the whole of that day and is hidden from local midnight after it, by itself; the card shows "Until <date>". The owner sees "Not showing: it ended on ..." and brings it back by choosing a new last day. Empty means until the owner turns it off.
- **Switch.** Off hides it without deleting it. A browser sends nothing for an unticked box, so "not sent" means off.
- **Serving.** A picture is served at `/banner/{slug}` (public, revalidated with an ETag each time) only while the banner is showing, so a paused or ended promo is "not found" at its address too. The owner's own preview (`/Admin/Promo?handler=Image`) is scoped to the signed-in shop and works while it is paused or ended. Nothing is shown when the shop is not taking bookings (trial ended or switched off).
- **Discount (optional).** A whole percentage from 1 to 90 (checked by the form and by the database). While the promo shows (switched on and not past its last day), it is taken off every service that is **part of the promo**, for bookings made then. Each service has a "Part of the promo" box on its edit page, on by default (so a promo covers the menu until the owner leaves a service out). The "Other" service and a free service never get a discount. The net price is rounded to the centavo (half a centavo rounds up); the same rule is in `VeskaFlow.Core.PromoPricing`, checked by the self-checks, and the page script uses the same whole-centavo arithmetic for the live price list. If the owner leaves the big badge empty, the card says "N% OFF" itself.
- **When the price is fixed.** At the moment the booking is made: what the customer saw is what they pay, whatever the day of the appointment, and a promo that changes or ends afterwards never touches a booking that exists. The booking keeps `price_snapshot` (what the customer pays, so every existing report and screen keeps working), `regular_price_snapshot` and `discount_percent_snapshot` (only when a promo applied). A staff walk-in made while the promo runs gets it too. A reschedule keeps the price.
- **Where it shows.** The regular price struck through, the price to pay and the discount ("20% off"): in the service list of the booking page ("₱120.00 (was ₱150.00)"), on the confirm page, on the customer's manage page, in the owner's day view and walk-in page, in the Promo Creator's price list (live as the discount is typed), and in what the AI assistant is told and tells the customer. The booking page never shows a discount that will not be given.
- **No link.** The banner has no link to anywhere, so it cannot send customers to another site.
- **Owner only.** Like the logo and the settings.
- **Not built:** several promos at once, a fixed peso discount, a link or "book this promo" button, scheduling a start day, a different discount per service, a Manager role editing it, uploading to a library. The Terms do not yet say that the owner is responsible for what a promo says or for having the right to the picture; add that, with the next legal review.

### 6.y19 Payment total and the services done on a visit

When staff mark a visit **Done**, the page that opens (`/Admin/AfterVisit/{id}`) shows what was done and what was paid.

**Services done (the itemized receipt).** A customer may book a haircut and also have a color. Staff add **another service** on this page, each as its own line with its own price, up to 10 per visit, and can remove a line again. A service from the list takes its own price, less the promo discount when a promo is running **today** and the service is part of it (6.y18); the "Other" service takes a description (1 to 60 characters) and a price (0 to 1,000,000) typed by staff and never gets a discount. The line keeps its name and price as they were when added (and the regular price and discount when a promo applied). Only a **completed** visit can have lines; a line belongs to a booking of the same business (the database ties them with a composite key, invariant I7) and is deleted with its booking.

**Payment total.** What the customer actually paid. It starts at the **items total** (the booked service after any promo, plus the added services) and staff change it when the customer got another discount or left a tip. Saved in `booking.paid_amount` (0 to 10,000,000, rounded to the centavo); empty clears it, and the items total counts again. A peso sign and thousands commas are accepted when typing. **When the lines change, a saved payment total goes back to the items total** (the page says so), so it can never be left behind by the services.

- Only a completed visit can have a payment total or services. The booking must be in the signed-in business, and a Staff login tied to one person can only change their own bookings' (another business's id and another person's booking are "not found").
- **Typed amounts are a Settings choice.** **Settings > "Staff can type the payment total and the price of an added service"** (`business.allow_manual_total`, default on). When on, staff may type the payment total and may type an **Amount** for any service they add (blank uses the listed price, less the promo as above; a typed amount is exactly what was charged, so no promo is applied on top, and it is rounded to the centavo, 0 to 1,000,000). When off, the Done page shows the total as the price of the services, the server refuses a typed payment total or a typed amount for a listed service (a forged post too), and a total typed earlier can be put back to the items total. The "Other" service always takes a typed price, because it has none. It is the shop's own choice and applies to every staff login of that shop.
- **Audit:** adding a service (`EXTRA_ADDED`), removing one (`EXTRA_REMOVED`) and setting or clearing the payment total (`PAYMENT_SET`, with what it was before) are written to the audit log with who did it.
- **Money rule:** every figure about money uses the payment total when there is one, otherwise the items total: the day view's revenue, the Reports revenue and a customer's total spent. In **Reports**, the lists by service and by staff member show the **prices of the services done** (a service done on top counts as one more of that service, credited to the booked staff member), and the part of the revenue that is not a service price (a tip, another discount typed in the total) is shown on its own as "Tips and other changes", so the two always add up to the revenue. Reports also show **Promo discounts given** (regular price minus price, on the booked service and on added ones, completed visits only).
- **The customer sees it.** Once the visit is done, the customer's manage page shows the booked service, each "Also done" line with its price, and **Total paid**.
- A customer's export carries the quoted price, the regular price, the discount, the payment total and the services done on top; deleting the customer deletes them.
- **Not built:** a payment method (cash, GCash), editing the price of a line after it was added (remove it and add it again), a receipt that can be printed or sent by message, and showing the added services in the customer's history page for staff.

### 6.y20 Record a sale by hand

For a service that was done and paid without a booking in the system, a shop can switch on **Settings > "Allow recording a sale by hand"** (`business.allow_manual_sales`, default off, migration 027). A **Record a sale** page (`/Admin/RecordSale`, Manager level and above) then takes the service (the "Other" service needs what was done), the **amount paid** (0 to 1,000,000), the date (today or earlier, at most 366 days back), an optional time, who did it, and the customer.

- It is saved as a **completed** visit with the amount as its payment total, so reports, the day's earnings and the customer's history count it like any other visit. It holds no schedule time (only confirmed bookings do) and sends nothing to the customer.
- **Customer:** with a mobile number the sale goes to that customer (matched by number; an existing customer keeps the name on file). With no number it is saved as a **walk-in** customer (name as typed, or "Walk-in"), each time its own customer, like any walk-in.
- When the switch is off the page does not exist (not found). The same form sent twice is one sale (idempotency key). Another shop's service or staff member is "not found". The entry is written to the audit log as `MANUAL_SALE` with who did it.

### 6.y21 Sending a promo to customers

The shop can send its running promo to customers, but only to those who **said yes** to promo messages, and the **system sends nothing itself**.

- **Consent.** The booking form has an unticked box "Send me promos and offers from <shop> by text or email" (never ticked for the customer; an empty box means no). The walk-in form has the same box for staff to tick after asking the customer. Staff can also record a yes, or a "stop", in the customer's history (`customer.marketing_ok`, `marketing_ok_at_utc`, migration 029; written to the audit log as `PROMO_CONSENT_ON` / `PROMO_CONSENT_OFF` with who did it). A later booking without the tick never takes a yes away; a stop is recorded by staff. Bookings made by the AI chat never carry a yes. A stop also clears the "sent" mark.
- **The page** (`/Admin/PromoSend`, Manager level and above, linked from the Promo page) works only while a promo is showing. It lists customers who said yes and can be reached (a real mobile number, or an email address), those not yet sent first, as one short line each; opening a line shows the ready message (the first name, the promo headline and details, the discount, the last day, the booking link and "Reply STOP if you do not want promo messages") with Copy, Send as text and Email buttons (staff send from their own phone or email program), and a **Mark as sent** button (`customer.promo_sent_at_utc`) so nobody is sent the same promo twice. **Clear all sent marks** starts a new promo. The list shows at most 200 customers, searchable by name, phone or email. Walk-ins with no number and no email are never listed.
- A customer who did not say yes can not be marked, listed or messaged through this page (not found). Another shop's customers are never reachable. The customer's data export shows whether they agreed, when, and when they were last sent a promo.
- **Not built:** sending by the system (email or SMS in bulk, which needs an email service with a daily limit, an unsubscribe link and a stronger consent record), scheduling, and a record of each message sent.

### 6.z Who may use the system

- A new owner must confirm the email address before signing in (`Signup:RequireEmailConfirmation`, default on). The link carries a random token; only its SHA-256 is stored; it works once and expires after 48 hours. Signing in with the right password before confirming says why and offers to send the link again; a wrong password gets the one generic message. "Send again" answers the same for any address and only mails an account that is waiting.
- A shop can be switched off by the platform owner (`business.is_enabled`): its owners are signed out on their next request and cannot sign in, online booking is closed, and nothing is deleted. Switching it on restores everything. With `Signup:RequireApproval` a new shop starts switched off.
- The platform owner is a confirmed, active owner whose email is listed in `Platform:AdminEmails`. `/Platform` lists every shop (owner, address, signed up, trial end, email confirmed, on/off) and can switch a shop on/off, confirm an owner's email by hand, and send or copy a new link. Anyone else gets "not found". The platform owner cannot switch off or delete their own shop. A shop can be deleted for good from /Platform (bookings, customers, staff, services, follow-ups, medical records, audit log and the owner's account, in one transaction): the page asks for the shop's booking address to be typed and shows a browser confirm window first; nothing is deleted if the address does not match. Only the platform owner can do this, and the address and email can be registered again afterwards.
- Emails go through the Brevo HTTP API (`Email:BrevoApiKey`, `Email:From`, `Email:FromName`; the same service the veskacode.com contact form uses), or Resend if Brevo is not configured. The email has a plain-text body and a "Confirm my email" button. The "check your email" page asks the server every few seconds whether the link was opened and then moves on to sign-in by itself (the address is never in a URL). With no email service configured nothing is emailed and the platform owner confirms by hand. Accounts that existed before this rule were marked confirmed (migration 009).

### 18.2 Sign-up and the 30-day trial

- Each sign-up creates its own tenant: its own business, owner login and booking link `/b/{slug}` (the slug is the public key, the owner login is the private one). Tenant isolation is unchanged. **Only the email is unique among people:** two shops may have the same name and two owners the same name; an email (in any capitals) can have one account. The **booking address is made from the shop name** (or from what was typed, if anything: whatever is typed is cleaned into a slug, capitals to small letters, spaces and symbols to single hyphens, cut at 40 characters, "San Juan Salon" becomes `san-juan-salon`) and is **never refused**: if the address is already used, a short code of four letters or numbers (none that look alike, for example `san-juan-salon-k7x2`) is added at the end, the name is cut so the address always fits in 40 characters, and a name too short or with no letters gets a stem and a code (a bare `shop` is never given out). Two sign-ups racing for one address both succeed, and the one that loses simply takes another code (a few attempts); two racing for one email give one success and a friendly "email taken" error. The owner sees the address in Settings and on the QR code.
- `Trial:Days` (configuration, default 30) sets `trial_ends_at_utc` at sign-up. `0` turns trials off, for example on a deployment the client hosts for itself. Existing shops have no trial date and are never limited.
- The admin shows the days left.
- **After the trial ends the account lapses.** The public page stops accepting customer bookings and says so, and every back-office page sends the owner to a "your free trial has ended" page with a way to contact the operator and a sign-out button. The server checks this against the database on every admin request (the cookie is only for the bell). Nothing is deleted: the data is kept. The operator extends or lifts a trial by setting or clearing `trial_ends_at_utc` for that business (see DEPLOY.md), and deletes a tenant by hand after confirming with the business.
- **A trial has every feature and no staff limit.** The one-staff limit of the FREE plan applies only to a business with no trial date.
- Sign-up is open (no invite code) while the URL is shared only with the business being shown the system. The sign-in and sign-up rate limit applies. An invite code can be added later if the URL becomes public.

### 18.3 Customer data

- A booking holds name, phone, optional email, service, staff member and time. No symptoms, diagnosis or health notes are collected; the MVP has no free-text note field.
- The confirm page shows a short privacy line (what is kept, why, and that deletion can be requested). Retention is as in section 13.
- Messages to customers are generic (date, time, business name) and are sent by the business through the manual links in section 8.5.

### 18.4 Automation labels

- Anything shown to a client as an automation is labelled **Built** (works in the current application) or **Preview** (planned). No Preview item is described as working. Nothing is sent to a customer automatically.

### 18.5 Hosting

- A business may run on the shared free hosting (section 17) or on hosting the client pays for. The application takes everything from configuration, so a client-hosted deployment needs no code change.
- Free hosting has no uptime guarantee, a slow first request after idle time and limited database history. The trial must say so.
- Before any real customer data is stored: the Neon safety gate in section 17.3 has passed and a `pg_dump` kept outside Neon has been restored once.

### 18.6 Not built

Invite codes, a consent record per booking, clinic-specific fields such as an intake form.

### 17.3.1 Result of the Neon safety gate (2026-10-25)

On the Neon Free project in AWS Singapore, **PostgreSQL 18.6**: `CREATE EXTENSION btree_gist` succeeded (version 1.8), `tests/db/double_booking.sh` printed "All database invariants hold." against a separate `veskaflow_test` database, and the whole Web test suite passed (91 of 91, direct connection, `SSL Mode=Require`). One earlier run lost two tests to a dropped connection while the development PC was asleep; the rerun passed. Not checked: a Neon branch restore, behaviour after the compute has been idle, and PostgreSQL 16 on Neon (the other tests ran on 16.11 locally).
