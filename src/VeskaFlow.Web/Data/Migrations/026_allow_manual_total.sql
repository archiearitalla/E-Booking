-- A shop's choice: may staff set the payment total by hand when a visit is done?
--   business.allow_manual_total   true (the default, so nothing changes for existing shops): staff can type what the customer paid.
--                                 false: the payment total is always what the services come to, and cannot be typed.
-- Additive only: one new column with a default.

ALTER TABLE business ADD COLUMN allow_manual_total boolean NOT NULL DEFAULT true;
