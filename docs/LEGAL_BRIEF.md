# Brief for a lawyer: VeskaFlow Privacy Notice and Terms of Service

This is a plain description of the system, written to make a legal review quick. It is not legal advice and it states no
legal conclusions. Items marked **(confirm)** are facts or choices the owner must verify before relying on them.

Texts to review: `src/VeskaFlow.Web/Pages/Privacy.cshtml` and `src/VeskaFlow.Web/Pages/Terms.cshtml` (live at
`/Privacy` and `/Terms`). Version of the texts: see `LegalInfo.Version`.

## 1. What the service is

VeskaFlow is a multi-tenant online booking system for barbershops, salons and clinics in the Philippines, run as a
subscription (software as a service). Each shop ("tenant") signs up itself, gets a 30-day free trial, and then pays a
monthly fee. Customers of a shop book through the shop's public page; the shop's staff use a password-protected back office.

Operator: **(confirm)** the registered name, address, DTI/BIR details and TIN of the person or business that runs it.
The legal pages take the name from the setting `Legal:OperatorName` and a contact from `Legal:ContactEmail`.

## 2. Who is who (as the texts currently say)

| Data | Controller | Processor |
|---|---|---|
| A shop's customers (name, phone, email, bookings, notes) | the shop | the operator |
| Shop owners and staff accounts (name, email, password hash) | the operator | providers below |

Question: is this split right, and does the Terms clause "Your customers' data (data processing)" satisfy the
requirement for a contract between a controller and a processor?

## 3. Data kept

- **Shop owners and staff:** name, email, password (salted hash only), the shop's name, address, hours, settings, an
  optional logo, and when and which version of the Terms they accepted.
- **Customers of a shop:** name, mobile number, optional email, bookings (service, staff member, time, status).
- **Optional notes by the shop:** visit notes and follow-ups. A clinic can record health information here, which is
  sensitive personal information. Visible only to that shop's signed-in staff. Never shown to the customer on the booking
  page and never put into a message by the system.
- **Technical:** audit log of booking changes (who, what, when); hosting request logs (time, network address) **(confirm
  provider's retention)**.
- No analytics, advertising or tracking cookies. Cookies used: sign-in, form security token, a short message cookie.

## 4. Where data goes (processors)

| Provider | Purpose | Data | Location |
|---|---|---|---|
| Render | application hosting | everything in transit and in memory | **(confirm region)** |
| Neon | PostgreSQL database | all stored data | Singapore (AWS ap-southeast-1) |
| Brevo | transactional email to shop owners (sign-up confirmation, password reset, notice before access ends) | owner's name, email, message | **(confirm: EU)** |
| Cloudflare | domain name; bot check (Turnstile) on sign-up and forgot-password when switched on | request data sent by the browser | global |
| GitHub | nightly encrypted database backup, kept 14 days | the whole database, **encrypted** (AES-256) before it leaves the runner; only the operator holds the passphrase | US **(confirm)** |
| UptimeRobot | checks that the site is up | only the site address (no personal data) | **(confirm)** |

The operator also saves an encrypted backup file to their own drive about once a month.

Question: are there cross-border transfer notices or agreements needed beyond the Privacy Notice sentence that some
providers keep data outside the Philippines?

## 5. What the app does for data subjects and security

- An owner agrees to the Terms and Privacy Notice by ticking a box at sign-up; the time and version are stored.
- The shop can **export** one customer's data as a file and **permanently delete** that customer with all their bookings
  and records (typing the customer's name to confirm). Only the owner role can do either.
- Deleting a shop (by the operator) deletes all of its data.
- Tenant isolation: staff reach only their own shop's data. Roles: owner, manager, staff.
- HTTPS, hashed passwords, rate limiting on sign-in, sign-up and booking, audit log, encrypted backups, forgot-password
  links valid for one hour and single use.
- A breach is to be reported to the affected shop "without undue delay" (Terms and Privacy Notice).

## 6. Choices the owner must confirm (they are written into the texts)

1. Data of an expired shop is kept **at least 90 days**, and the owner is emailed before an inactive shop is deleted.
2. Saved monthly backup copies are kept **up to 12 months**; online backups 14 days.
3. Fees are paid in advance, **not refundable** once a period has started, unless the law requires otherwise.
4. Price changes announced **30 days** ahead.
5. Minimum age to sign up: **18**.
6. Liability limited to the fees of the previous **3 months**.
7. Payment is manual (GCash to the operator); no automatic charge yet. The operator issues receipts or invoices as their
   BIR registration allows **(confirm)**.
8. Governing law: Philippines. A venue or dispute clause is **not** written yet.

## 7. Questions for the lawyer

1. Does the operator need to **register** with the National Privacy Commission, and **designate a data protection
   officer**? Does processing clinics' health information change that? What must the DPO contact in the notice look like?
2. Are the Privacy Notice and Terms enough as the shop's and the operator's notices? What must be added for health
   information and for minors?
3. Is acceptance by a tick box (with version and time stored) good enough for these Terms (E-Commerce Act)? Should existing
   shops be asked to accept a new version, and how?
4. Is the limitation of liability, the no-refund clause and the 90-day / 12-month retention enforceable and reasonable?
5. The breach duties (to the shop, and where the operator is the controller, to the NPC and individuals within 72 hours):
   is the wording right, and is a written breach procedure needed?
6. Any consumer or other rules for subscription fees, automatic renewal (planned later) and price changes?
7. A venue clause, an indemnity clause, and anything usual in a software subscription that is missing.

## 8. Non-legal steps to confirm with an accountant (before taking money)

- Business registration (DTI or SEC), BIR registration, TIN, and how receipts or invoices are issued for subscription fees.
- Local permits that apply to the operator.
- How to treat payments received through GCash for tax purposes.
