-- Promo discount and the payment total.
--   business_banner.discount_percent        the promo's discount, 1 to 90 percent (empty = no discount, the banner is only a banner).
--   service.promo_eligible                  whether the service is part of the promo (default yes, so a promo covers the whole menu
--                                           until the owner leaves a service out).
--   booking.regular_price_snapshot          the service's regular price when the booking was made, kept only when a promo applied.
--   booking.discount_percent_snapshot       the discount that applied. price_snapshot stays what the customer pays (the net price),
--                                           so every existing report and screen keeps working.
--   booking.paid_amount                     the payment total staff entered when the visit was done (empty = the net price).
-- Additive only: new columns, nothing is changed or removed. Existing rows keep their values; no existing booking is touched.

ALTER TABLE business_banner ADD COLUMN discount_percent integer NULL;
ALTER TABLE business_banner ADD CONSTRAINT business_banner_discount CHECK (discount_percent IS NULL OR discount_percent BETWEEN 1 AND 90);

ALTER TABLE service ADD COLUMN promo_eligible boolean NOT NULL DEFAULT true;

ALTER TABLE booking ADD COLUMN regular_price_snapshot numeric(10, 2) NULL;
ALTER TABLE booking ADD COLUMN discount_percent_snapshot integer NULL;
ALTER TABLE booking ADD COLUMN paid_amount numeric(10, 2) NULL;
ALTER TABLE booking ADD CONSTRAINT booking_discount_pair CHECK ((regular_price_snapshot IS NULL) = (discount_percent_snapshot IS NULL));
ALTER TABLE booking ADD CONSTRAINT booking_discount_range CHECK (discount_percent_snapshot IS NULL OR discount_percent_snapshot BETWEEN 1 AND 90);
ALTER TABLE booking ADD CONSTRAINT booking_paid_amount CHECK (paid_amount IS NULL OR paid_amount BETWEEN 0 AND 10000000);
