-- Follow-up after a completed visit (for example a clinic check-up in two weeks).
-- A service may suggest a follow-up after N days; staff can set or change the follow-up date on a completed booking.
-- Additive only: new nullable columns and one column with a default.

ALTER TABLE service ADD COLUMN follow_up_days integer NULL;
ALTER TABLE service ADD CONSTRAINT service_follow_up_days CHECK (follow_up_days IS NULL OR follow_up_days BETWEEN 1 AND 730);

ALTER TABLE booking ADD COLUMN follow_up_on   date NULL;
ALTER TABLE booking ADD COLUMN follow_up_note text NULL;
ALTER TABLE booking ADD COLUMN follow_up_done boolean NOT NULL DEFAULT false;
ALTER TABLE booking ADD CONSTRAINT booking_follow_up_note CHECK (follow_up_note IS NULL OR length(follow_up_note) <= 200);

-- The follow-up list reads only bookings that still have a follow-up to do.
CREATE INDEX ix_booking_follow_up ON booking (business_id, follow_up_on) WHERE follow_up_on IS NOT NULL AND NOT follow_up_done;
