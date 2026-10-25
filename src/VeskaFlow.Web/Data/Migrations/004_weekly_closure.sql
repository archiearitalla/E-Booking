-- Closures that repeat: "closed every Sunday", or "closed 12:00 to 13:00 every day", for the whole shop or one staff member.
-- day_of_week NULL = every day. start_time/end_time both NULL = the whole day, both set = only that part of the day.
-- Each can be switched off and on at any time without deleting it. Additive only: one new table.

CREATE TABLE weekly_closure (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id    uuid NOT NULL REFERENCES business (id),
    staff_id       uuid NULL,                      -- NULL = whole shop
    day_of_week    smallint NULL,                  -- 0 = Sunday ... 6 = Saturday (System.DayOfWeek); NULL = every day
    start_time     time NULL,                      -- local wall-clock time in the business time zone
    end_time       time NULL,
    reason         text NULL,
    is_active      boolean NOT NULL DEFAULT true,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (staff_id, business_id) REFERENCES staff (id, business_id) ON DELETE CASCADE,
    CONSTRAINT weekly_closure_dow CHECK (day_of_week IS NULL OR day_of_week BETWEEN 0 AND 6),
    CONSTRAINT weekly_closure_times CHECK (
        (start_time IS NULL AND end_time IS NULL) OR (start_time IS NOT NULL AND end_time IS NOT NULL AND start_time < end_time)),
    CONSTRAINT weekly_closure_reason CHECK (reason IS NULL OR length(reason) <= 120)
);

-- One rule per person (or whole shop), weekday and time window.
CREATE UNIQUE INDEX ux_weekly_closure
    ON weekly_closure (business_id, COALESCE(staff_id, '00000000-0000-0000-0000-000000000000'::uuid),
                       COALESCE(day_of_week, -1), COALESCE(start_time, '00:00'::time), COALESCE(end_time, '00:00'::time));
