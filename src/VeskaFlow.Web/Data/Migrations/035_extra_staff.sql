-- Staff included in every shop's price is 5; more staff cost extra (BUSINESS_RULES 7.2). The platform owner sets how many EXTRA staff a shop
-- paid for, by hand, after the payment. Additive. 0 for every existing shop. The limit stops one shop from filling the database.
ALTER TABLE business ADD COLUMN extra_staff integer NOT NULL DEFAULT 0 CONSTRAINT business_extra_staff_range CHECK (extra_staff BETWEEN 0 AND 100);
