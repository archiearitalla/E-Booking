-- Automatic promo emails (BUSINESS_RULES 6.y21). marketing_token is the secret in a customer's unsubscribe link; promo_email_started_at_utc
-- is set while a shop's promo email is being sent in the background (null = nothing queued). Additive only.
ALTER TABLE customer ADD COLUMN marketing_token text NULL;
CREATE UNIQUE INDEX ux_customer_marketing_token ON customer (marketing_token) WHERE marketing_token IS NOT NULL;
ALTER TABLE business ADD COLUMN promo_email_started_at_utc timestamptz NULL;
