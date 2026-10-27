-- Links a back-office user with the STAFF role to the bookable staff member they are (for example a barber), so they see
-- only that person's bookings. Null means no link: the user sees the whole shop as before. Additive only: one nullable
-- column and one index.

ALTER TABLE admin_user ADD COLUMN staff_id uuid NULL REFERENCES staff (id);

CREATE INDEX ix_admin_user_staff ON admin_user (staff_id) WHERE staff_id IS NOT NULL;
