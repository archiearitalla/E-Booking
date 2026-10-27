-- Forgot password: a one-hour link emailed to the account's address. Only the SHA-256 of the link token is stored.
-- Additive only: two nullable columns and one index.

ALTER TABLE admin_user ADD COLUMN password_reset_hash text NULL;
ALTER TABLE admin_user ADD COLUMN password_reset_expires_utc timestamptz NULL;

CREATE UNIQUE INDEX ux_admin_user_password_reset ON admin_user (password_reset_hash) WHERE password_reset_hash IS NOT NULL;
