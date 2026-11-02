-- Waitlist (BUSINESS_RULES 7.3): a customer leaves a name and contact for a full day, and the shop sees who wants a slot. One row per request.
-- "day" is the shop's local date. Entries are removed by the shop or purged after their date. Additive. Nothing is written unless a customer joins.
CREATE TABLE waitlist_entry (
    id               uuid        PRIMARY KEY,
    business_id      uuid        NOT NULL REFERENCES business (id),
    service_id       uuid        NOT NULL REFERENCES service (id),
    staff_id         uuid        NULL REFERENCES staff (id),
    day              date        NOT NULL,
    customer_name    text        NOT NULL,
    customer_phone   text        NOT NULL,
    customer_email   text        NULL,
    created_at_utc   timestamptz NOT NULL,
    notified_at_utc  timestamptz NULL,
    CONSTRAINT waitlist_entry_name  CHECK (length(customer_name) BETWEEN 1 AND 80),
    CONSTRAINT waitlist_entry_phone CHECK (length(customer_phone) BETWEEN 1 AND 20),
    CONSTRAINT waitlist_entry_email CHECK (customer_email IS NULL OR length(customer_email) BETWEEN 3 AND 120)
);
CREATE INDEX ix_waitlist_entry_business_day ON waitlist_entry (business_id, day);
CREATE INDEX ix_waitlist_entry_phone ON waitlist_entry (business_id, customer_phone);
