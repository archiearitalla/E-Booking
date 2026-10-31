-- Billing (BUSINESS_RULES 6.y22): what clients pay for their plan and add-ons, as a ledger. A client says "I paid" (PENDING) with the payment's
-- reference number; the platform owner confirms it (CONFIRMED: access or the add-on is extended and a receipt number is given) or rejects it.
-- Additive only.
CREATE SEQUENCE payment_receipt_seq;

CREATE TABLE payment (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id      uuid          NOT NULL REFERENCES business (id),
    kind             text          NOT NULL,
    period           text          NOT NULL,
    days             integer       NOT NULL,
    amount           numeric(10,2) NOT NULL,
    reference        text          NOT NULL,
    note             text          NULL,
    status           text          NOT NULL,
    by_hand          boolean       NOT NULL DEFAULT false,
    submitted_at_utc timestamptz   NOT NULL,
    submitted_by     text          NOT NULL,
    decided_at_utc   timestamptz   NULL,
    decided_by       text          NULL,
    reject_reason    text          NULL,
    receipt_no       text          NULL,
    CONSTRAINT payment_kind   CHECK (kind IN ('PRO', 'AI', 'EMAIL')),
    CONSTRAINT payment_period CHECK (period IN ('MONTH', 'YEAR')),
    CONSTRAINT payment_days   CHECK (days IN (30, 365)),
    CONSTRAINT payment_amount CHECK (amount BETWEEN 0 AND 1000000),
    CONSTRAINT payment_status CHECK (status IN ('PENDING', 'CONFIRMED', 'REJECTED')),
    CONSTRAINT payment_reference CHECK (length(reference) BETWEEN 1 AND 60),
    CONSTRAINT payment_note   CHECK (note IS NULL OR length(note) <= 200),
    CONSTRAINT payment_receipt CHECK ((status = 'CONFIRMED') = (receipt_no IS NOT NULL))
);
CREATE UNIQUE INDEX ux_payment_receipt_no ON payment (receipt_no) WHERE receipt_no IS NOT NULL;
CREATE INDEX ix_payment_business ON payment (business_id, submitted_at_utc DESC);
CREATE INDEX ix_payment_pending ON payment (status) WHERE status = 'PENDING';
