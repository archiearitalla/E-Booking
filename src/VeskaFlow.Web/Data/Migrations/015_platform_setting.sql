-- Small notes the platform owner keeps about the whole system (not about one shop), for example when a copy of the
-- backup was last saved. Additive only: one new table.

CREATE TABLE platform_setting (
    key            text PRIMARY KEY,
    value          text NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_setting_key CHECK (length(key) BETWEEN 1 AND 60)
);
