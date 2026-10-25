-- The shop's opening hours, set once in Settings. They are copied to every staff member's weekly hours and to new staff.
-- Same shape as working_hour (a break is two windows on the same day). Additive only: one new table.

CREATE TABLE business_hour (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id uuid NOT NULL REFERENCES business (id),
    day_of_week smallint NOT NULL,                 -- 0 = Sunday ... 6 = Saturday (System.DayOfWeek)
    open_time   time NOT NULL,
    close_time  time NOT NULL,
    CONSTRAINT business_hour_dow CHECK (day_of_week BETWEEN 0 AND 6),
    CONSTRAINT business_hour_times CHECK (open_time <> close_time)
);

CREATE INDEX ix_business_hour_business ON business_hour (business_id, day_of_week);
