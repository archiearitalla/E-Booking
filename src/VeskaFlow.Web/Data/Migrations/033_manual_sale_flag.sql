-- A sale recorded by hand ("Record a sale") is a completed visit that was done at that time. While its time is still running it must
-- block that time for new bookings (BUSINESS_RULES 6.y20). The flag tells it apart from an ordinary booking marked completed early.
-- Additive; the existing manual sales are marked from their audit rows.
ALTER TABLE booking ADD COLUMN is_manual_sale boolean NOT NULL DEFAULT false;
UPDATE booking SET is_manual_sale = true
WHERE id IN (SELECT booking_id FROM audit_log WHERE action = 'MANUAL_SALE' AND booking_id IS NOT NULL);
