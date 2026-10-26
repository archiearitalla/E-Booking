-- When the owner agreed to the Terms and the Privacy Notice at sign-up, and which version of them. Null for shops that
-- signed up before this existed. Additive only: two nullable columns.

ALTER TABLE business ADD COLUMN terms_accepted_at_utc timestamptz NULL;
ALTER TABLE business ADD COLUMN terms_version text NULL;
