-- Reminder emails to a shop's owner before their access ends (7, 3 and 1 days). These two columns remember which end date
-- the last reminder was for and how many days were left, so no reminder is sent twice and an extension starts over.
-- Additive only: two nullable columns.

ALTER TABLE business ADD COLUMN expiry_notice_days integer NULL;
ALTER TABLE business ADD COLUMN expiry_notice_for_end_utc timestamptz NULL;
