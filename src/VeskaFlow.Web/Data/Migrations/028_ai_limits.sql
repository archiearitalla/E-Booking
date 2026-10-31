-- Per-shop AI receptionist limits and a per-day count (BUSINESS_RULES 6.y17). A null limit means "use the default from settings". Additive only.
ALTER TABLE business ADD COLUMN ai_daily_limit   integer NULL;
ALTER TABLE business ADD COLUMN ai_monthly_limit integer NULL;
ALTER TABLE business ADD COLUMN ai_token_limit   bigint  NULL;
ALTER TABLE business ADD CONSTRAINT business_ai_limits CHECK (
    (ai_daily_limit   IS NULL OR ai_daily_limit   BETWEEN 0 AND 100000) AND
    (ai_monthly_limit IS NULL OR ai_monthly_limit BETWEEN 0 AND 1000000) AND
    (ai_token_limit   IS NULL OR ai_token_limit   BETWEEN 0 AND 10000000000));

CREATE TABLE ai_usage_day (
    day           date    NOT NULL,
    business_id   uuid    NOT NULL,
    conversations integer NOT NULL DEFAULT 0,
    PRIMARY KEY (day, business_id),
    CONSTRAINT ai_usage_day_counts CHECK (conversations >= 0)
);
