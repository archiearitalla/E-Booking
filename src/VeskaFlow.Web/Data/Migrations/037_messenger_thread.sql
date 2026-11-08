-- Messenger (BUSINESS_RULES 6.y23): one row per person who messaged the shop's Facebook Page, tying their Page-scoped id (psid) to their conversation with
-- the AI receptionist, and the id of the last message handled (Meta delivers a message again when it does not get an answer in time).
-- Additive. Nothing is written unless the Messenger settings are filled in.
CREATE TABLE messenger_thread (
    business_id    uuid        NOT NULL REFERENCES business (id),
    psid           text        NOT NULL,
    chat_id        uuid        NULL,
    last_mid       text        NULL,
    updated_at_utc timestamptz NOT NULL,
    PRIMARY KEY (business_id, psid),
    CONSTRAINT messenger_thread_psid CHECK (length(psid) BETWEEN 1 AND 100)
);
CREATE INDEX ix_messenger_thread_updated ON messenger_thread (business_id, updated_at_utc);
