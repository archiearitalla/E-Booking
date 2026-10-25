-- Who may use the system: a shop can be switched off by the platform owner, and a new owner must confirm their email
-- address before signing in. Additive only: new columns and one index. People who already have an account are marked
-- as confirmed (they were let in before this rule existed).

ALTER TABLE business ADD COLUMN is_enabled boolean NOT NULL DEFAULT true;

ALTER TABLE admin_user ADD COLUMN email_confirmed_at_utc timestamptz NULL;
ALTER TABLE admin_user ADD COLUMN email_token_hash text NULL;          -- SHA-256 of the link token; the token itself is never stored
ALTER TABLE admin_user ADD COLUMN email_token_expires_utc timestamptz NULL;

UPDATE admin_user SET email_confirmed_at_utc = now() WHERE email_confirmed_at_utc IS NULL;

CREATE UNIQUE INDEX ux_admin_user_email_token ON admin_user (email_token_hash) WHERE email_token_hash IS NOT NULL;
