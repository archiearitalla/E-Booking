-- Automatic emails to a shop's customers (booking confirmation and reminder), with a daily limit.
--   business.customer_emails         the shop's switch (default on)
--   business.email_addon_until_utc   the paid "more emails" add-on: until when it is active (null = not bought)
--   email_daily                      how many emails were sent per day, per shop (all zeros uuid = the system's own emails
--                                    to shop owners), so the daily limit of the email service is never passed.
-- Additive only: two columns and one new table.

ALTER TABLE business ADD COLUMN customer_emails boolean NOT NULL DEFAULT true;
ALTER TABLE business ADD COLUMN email_addon_until_utc timestamptz NULL;

CREATE TABLE email_daily (
    day         date    NOT NULL,
    business_id uuid    NOT NULL,
    sent        integer NOT NULL DEFAULT 0,
    PRIMARY KEY (day, business_id),
    CONSTRAINT email_daily_sent CHECK (sent >= 0)
);
