-- Outgoing webhooks (BUSINESS_RULES 6.y24): a shop (or the platform owner) can have VeskaFlow post events such as "booking.created" to an address, for
-- n8n or a similar tool. webhook_event is the outbox: events wait here until they are delivered, with retries. source_audit_id makes sure one audit
-- row becomes one event. No foreign key on business_id: a deleted shop's events are simply dropped by the worker. Additive only.
ALTER TABLE business ADD COLUMN webhook_url     text    NULL;
ALTER TABLE business ADD COLUMN webhook_secret  text    NULL;
ALTER TABLE business ADD COLUMN webhook_enabled boolean NOT NULL DEFAULT false;
ALTER TABLE business ADD CONSTRAINT business_webhook_url CHECK (webhook_url IS NULL OR length(webhook_url) <= 300);

CREATE TABLE webhook_event (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id         uuid        NULL,
    event               text        NOT NULL,
    payload             text        NOT NULL,
    status              text        NOT NULL DEFAULT 'PENDING',
    attempts            integer     NOT NULL DEFAULT 0,
    created_at_utc      timestamptz NOT NULL,
    next_attempt_at_utc timestamptz NOT NULL,
    delivered_at_utc    timestamptz NULL,
    last_status_code    integer     NULL,
    last_error          text        NULL,
    source_audit_id     bigint      NULL,
    CONSTRAINT webhook_event_status CHECK (status IN ('PENDING', 'DELIVERED', 'FAILED')),
    CONSTRAINT webhook_event_attempts CHECK (attempts >= 0)
);
CREATE UNIQUE INDEX ux_webhook_event_audit ON webhook_event (source_audit_id) WHERE source_audit_id IS NOT NULL;
CREATE INDEX ix_webhook_event_due ON webhook_event (next_attempt_at_utc) WHERE status = 'PENDING';
CREATE INDEX ix_webhook_event_business ON webhook_event (business_id, created_at_utc DESC);
