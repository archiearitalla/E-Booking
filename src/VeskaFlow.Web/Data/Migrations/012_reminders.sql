-- Reminders for customers before their appointment. The shop sends them itself (text, email, Messenger or any app); the
-- system only lists who is due and remembers who was already reminded. Additive only.

ALTER TABLE booking ADD COLUMN reminded_at_utc timestamptz NULL;

ALTER TABLE business ADD COLUMN reminder_hours integer NOT NULL DEFAULT 24;
ALTER TABLE business ADD CONSTRAINT business_reminder_hours CHECK (reminder_hours BETWEEN 1 AND 168);
