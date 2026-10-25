-- Optional medical record / visit notes on the day of the service (for the customer's history), and the service a
-- follow-up is for. Both are written only by signed-in staff and are never shown to customers or copied into messages.
-- Additive only: new nullable columns.

ALTER TABLE booking ADD COLUMN visit_notes text NULL;
ALTER TABLE booking ADD CONSTRAINT booking_visit_notes CHECK (visit_notes IS NULL OR length(visit_notes) <= 4000);

ALTER TABLE booking ADD COLUMN follow_up_service text NULL;
ALTER TABLE booking ADD CONSTRAINT booking_follow_up_service CHECK (follow_up_service IS NULL OR length(follow_up_service) <= 80);

-- A customer's history reads their completed visits, newest first.
CREATE INDEX ix_booking_customer_history ON booking (business_id, customer_id, starts_at_utc DESC);
