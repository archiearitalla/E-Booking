-- A client can pay for extra staff in Billing (BUSINESS_RULES 7.2): kind STAFF, with a quantity (how many extra staff, in total, for the period).
-- Confirming it sets business.extra_staff to that number until business.extra_staff_until_utc (30 days, renewed by paying again). Additive:
-- one column on each table and a WIDER list of payment kinds (nothing is removed). A null extra_staff_until_utc means the extra staff were set by
-- hand by the platform owner and do not expire.
ALTER TABLE business ADD COLUMN extra_staff_until_utc timestamptz NULL;
ALTER TABLE payment ADD COLUMN quantity integer NOT NULL DEFAULT 1 CONSTRAINT payment_quantity CHECK (quantity BETWEEN 1 AND 100);
ALTER TABLE payment DROP CONSTRAINT payment_kind;
ALTER TABLE payment ADD CONSTRAINT payment_kind CHECK (kind IN ('PRO', 'AI', 'EMAIL', 'STAFF'));
