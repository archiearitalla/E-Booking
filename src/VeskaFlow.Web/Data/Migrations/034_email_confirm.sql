-- Optional per-shop switch: a customer's online booking is held until they press the link sent to their email (BUSINESS_RULES 8.8).
-- Additive. Off by default, so nothing changes for existing shops.
-- business.require_email_confirm: the switch.
-- booking.email_confirm_hash: SHA-256 (hex) of the secret in the emailed link; the secret itself is only ever in the email.
-- The existing booking.hold_expires_at_utc says when an unconfirmed (HELD) booking is released.
ALTER TABLE business ADD COLUMN require_email_confirm boolean NOT NULL DEFAULT false;
ALTER TABLE booking ADD COLUMN email_confirm_hash text NULL;
CREATE UNIQUE INDEX ux_booking_email_confirm_hash ON booking (email_confirm_hash) WHERE email_confirm_hash IS NOT NULL;
