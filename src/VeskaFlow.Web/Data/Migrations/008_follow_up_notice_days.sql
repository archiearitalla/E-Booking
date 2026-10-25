-- How many days before a follow-up date the shop wants to be reminded to send the customer a message.
-- Additive only: one new column with a default (3 days, what the app used before it was a setting).

ALTER TABLE business ADD COLUMN follow_up_notice_days integer NOT NULL DEFAULT 3;
ALTER TABLE business ADD CONSTRAINT business_follow_up_notice_days CHECK (follow_up_notice_days BETWEEN 0 AND 60);
