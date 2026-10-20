-- Industry (selects default wording and starter service only) and an optional trial end date.
-- Additive only: two new columns with defaults, so existing shops keep working unchanged.

ALTER TABLE business
    ADD COLUMN industry text NOT NULL DEFAULT 'barbershop';

ALTER TABLE business
    ADD CONSTRAINT business_industry CHECK (industry IN ('barbershop', 'salon', 'clinic'));

-- NULL means no trial limit (existing shops, or a dedicated deployment with trials switched off).
ALTER TABLE business
    ADD COLUMN trial_ends_at_utc timestamptz NULL;
