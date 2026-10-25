-- An "Other" service for visits whose service is not in the shop's list. The customer (or staff for a walk-in) picks
-- "Other" and types what they need; the booking keeps that text. The owner sets how long "Other" takes and can switch it off.
-- Additive only: one new column, one index, and one "Other" service (linked to every staff member) for each existing shop.

ALTER TABLE service ADD COLUMN is_other boolean NOT NULL DEFAULT false;
CREATE UNIQUE INDEX ux_service_other ON service (business_id) WHERE is_other;

INSERT INTO service (id, business_id, name, duration_minutes, buffer_minutes, price, is_active, sort_order, is_other)
SELECT gen_random_uuid(), b.id, 'Other', 30, 0, 0, true, 1000, true FROM business b;

INSERT INTO staff_service (staff_id, service_id, business_id)
SELECT s.id, o.id, s.business_id FROM staff s JOIN service o ON o.business_id = s.business_id AND o.is_other;
