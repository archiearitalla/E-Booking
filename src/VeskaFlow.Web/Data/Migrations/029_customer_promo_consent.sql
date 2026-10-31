-- Promo messages to customers (BUSINESS_RULES 6.y21). A customer is on the promo list only after saying yes (a box on the booking form, or staff
-- recording it). promo_sent_at_utc marks who was already sent the current promo. Additive only.
ALTER TABLE customer ADD COLUMN marketing_ok        boolean     NOT NULL DEFAULT false;
ALTER TABLE customer ADD COLUMN marketing_ok_at_utc timestamptz NULL;
ALTER TABLE customer ADD COLUMN promo_sent_at_utc   timestamptz NULL;
