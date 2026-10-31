-- A shop may switch on "Record a sale" (BUSINESS_RULES 6.y20): staff with the right level enter a service and a payment by hand,
-- for the sales and customer records. Off by default. Additive.
ALTER TABLE business ADD COLUMN allow_manual_sales boolean NOT NULL DEFAULT false;
