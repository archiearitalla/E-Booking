# BarberBook — Business Rules

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
| Admin back office (server-rendered) | Owner (later manager/staff) | Calendar, status changes, configuration, audit | ✅ (OWNER only; no walk-in UI yet) |
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
2. It does not overlap a blackout (shop-wide or this staff member).
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
- **Staff-created bookings** (walk-ins, phone-ins) skip the lead time, so a booking can start right now. They still must be inside working hours, outside blackouts and free of overlaps, and the database constraint still applies. "Right now" means the slot already in progress: a walk-in may start up to one slot length (the business's slot size) before the current time (decided by the owner). The actor is recorded as `STAFF` in the audit log.
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

- MVP: pay on site. Prices are shown in pesos (₱); the currency is not configurable yet.
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
| Availability, DST, buffers, blackouts, lead time, horizon | ✅ 55 self-checks pass (`tests/BarberBook.Core.SelfCheck`) |
| State machine, cancellation and no-show policy, passwords, slugs | ✅ same run |
| Database invariants (race, overlaps, buffers, lifecycle, cross-tenant, constraints) | ✅ 20 checks pass against PostgreSQL 16 (`tests/db/double_booking.sh`) |
| EF mapping and SQL migrator (`Data/*`) | ✅ compiled; `tests/BarberBook.Web.ModelCheck` passes against PostgreSQL 16 (all tables, `xmin`, booking round trip, migrator idempotent) |
| `BookingService` (availability, create, idempotency, transitions, audit) | ✅ 21 integration tests against PostgreSQL 16, including 50 parallel requests for one slot |
| Registration, sign-in/out, cookie claims, `/Admin` protection, sign-in rate limit, antiforgery | ✅ 20 tests over services and HTTP against PostgreSQL 16 (`Secure` cookie flag and real-browser rendering not verified) |
| Public booking pages (`/b/{slug}`, confirm, `/booking/{token}`) | ✅ 12 HTTP tests plus a real-browser run at phone width; not verified: stopwatch timing, Safari or a real phone, screen reader |
| Admin back office (day view, services, staff and hours, closures, settings, audit, About) | ✅ 30 tests incl. tenant isolation on every endpoint, plan limit, "never silently cancels" warnings; not verified: screen reader, keyboard-only walkthrough of every form, Safari or real phones |
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

- A business has an `industry`: `barbershop`, `salon` or `clinic`, chosen at sign-up.
- The industry only chooses the starter service (Haircut, Haircut and styling, Consultation). It never changes the booking rules in sections 5 to 8 or the invariants in section 4. Everything can be edited afterwards.

### 18.2 Sign-up and the 30-day trial

- Each sign-up creates its own tenant: its own business, owner login and booking link `/b/{slug}` (the slug is the public key, the owner login is the private one). Tenant isolation is unchanged.
- `Trial:Days` (configuration, default 30) sets `trial_ends_at_utc` at sign-up. `0` turns trials off, for example on a deployment the client hosts for itself. Existing shops have no trial date and are never limited.
- The admin shows the days left.
- **After the trial ends:** the public page stops accepting new customer bookings and says so. Staff can still manage and create bookings. Nothing is deleted automatically; the operator deletes a tenant's data by hand, after confirming with the business.
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

Invite codes, an export of business data at the end of the trial, a consent record per booking, a higher staff limit for trials (FREE stays at 1 staff member), clinic-specific fields such as an intake form.
