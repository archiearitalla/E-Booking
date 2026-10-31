-- Services done on top of the booked one (an itemized receipt): a customer books a haircut and also has a color. Staff add the
-- extra service on the page that opens after Done, and it becomes its own line with its own price, so reports and the customer's
-- receipt show what was really done.
--   booking_extra     one row per extra service: the service, its name and price as they were when added (and the regular price and
--                     discount when a promo applied), tied to a booking of the same business. Deleted with its booking.
--   ux_booking_id_business   lets the extra be tied to a booking of the SAME business (tenant isolation, invariant I7).
-- Additive only: one new table and one new unique constraint on a column pair that is already unique (the id).

ALTER TABLE booking ADD CONSTRAINT ux_booking_id_business UNIQUE (id, business_id);

CREATE TABLE booking_extra (
    id                        uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id               uuid          NOT NULL REFERENCES business (id),
    booking_id                uuid          NOT NULL,
    service_id                uuid          NOT NULL,
    service_name_snapshot     text          NOT NULL,
    price_snapshot            numeric(10, 2) NOT NULL,
    regular_price_snapshot    numeric(10, 2) NULL,
    discount_percent_snapshot integer       NULL,
    created_at_utc            timestamptz   NOT NULL DEFAULT now(),
    FOREIGN KEY (booking_id, business_id) REFERENCES booking (id, business_id) ON DELETE CASCADE,
    FOREIGN KEY (service_id, business_id) REFERENCES service (id, business_id),
    CONSTRAINT booking_extra_price CHECK (price_snapshot BETWEEN 0 AND 1000000),
    CONSTRAINT booking_extra_name CHECK (char_length(service_name_snapshot) BETWEEN 1 AND 140),
    CONSTRAINT booking_extra_discount_pair CHECK ((regular_price_snapshot IS NULL) = (discount_percent_snapshot IS NULL)),
    CONSTRAINT booking_extra_discount_range CHECK (discount_percent_snapshot IS NULL OR discount_percent_snapshot BETWEEN 1 AND 90)
);
CREATE INDEX ix_booking_extra_booking ON booking_extra (booking_id);
