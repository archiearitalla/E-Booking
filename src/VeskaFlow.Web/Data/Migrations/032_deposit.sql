-- Optional deposit per service, paid by hand (BUSINESS_RULES 7.1). Additive.
-- service.deposit_amount: what the shop asks to keep a slot (0 = no deposit).
-- booking.deposit_snapshot: the deposit as it was when the booking was made (never above the price).
-- booking.deposit_received_at_utc: set by staff when the money arrived; null = not yet.
-- business.pay_instructions: where the customer sends the deposit (for example a GCash number and name).
ALTER TABLE service ADD COLUMN deposit_amount numeric(10,2) NOT NULL DEFAULT 0 CONSTRAINT service_deposit_nonneg CHECK (deposit_amount >= 0);
ALTER TABLE booking ADD COLUMN deposit_snapshot numeric(10,2) NOT NULL DEFAULT 0 CONSTRAINT booking_deposit_nonneg CHECK (deposit_snapshot >= 0);
ALTER TABLE booking ADD COLUMN deposit_received_at_utc timestamptz NULL;
ALTER TABLE business ADD COLUMN pay_instructions text NULL;
