-- AI receptionist: a paid add-on that answers customers in a chat on the shop's booking page and books through the same
-- booking service as everyone else.
--   business.ai_addon_until_utc   until when the add-on is switched on (null = never bought). The platform owner switches it
--                                 on after payment, like the email add-on.
--   ai_usage                      per shop and UTC month: conversations started and tokens used (limit and cost tracking).
--   ai_chat                       one customer conversation. The transcript is plain text only (what the customer typed and
--                                 what the assistant said) and is deleted after 30 days by the application.
-- Additive only: one column and two new tables.

ALTER TABLE business ADD COLUMN ai_addon_until_utc timestamptz NULL;

CREATE TABLE ai_usage (
    month         date    NOT NULL,
    business_id   uuid    NOT NULL,
    conversations integer NOT NULL DEFAULT 0,
    input_tokens  bigint  NOT NULL DEFAULT 0,
    output_tokens bigint  NOT NULL DEFAULT 0,
    PRIMARY KEY (month, business_id),
    CONSTRAINT ai_usage_counts CHECK (conversations >= 0 AND input_tokens >= 0 AND output_tokens >= 0)
);

CREATE TABLE ai_chat (
    id             uuid        PRIMARY KEY,
    business_id    uuid        NOT NULL REFERENCES business (id),
    transcript     text        NOT NULL DEFAULT '[]',
    turns          integer     NOT NULL DEFAULT 0,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    CONSTRAINT ai_chat_turns CHECK (turns >= 0)
);

CREATE INDEX ai_chat_business_created ON ai_chat (business_id, created_at_utc);
