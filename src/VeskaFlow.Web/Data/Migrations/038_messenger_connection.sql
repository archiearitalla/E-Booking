-- Messenger per shop (BUSINESS_RULES 6.y23): a shop owner connects their own Facebook Page(s) from Settings. One row per connected Page; a Page belongs to
-- one shop only, a shop may connect several Pages. The Page's access token is kept encrypted (AES-GCM, key from the Messenger:TokenKey setting), never in clear text.
-- Additive. Nothing is written unless the Messenger connect settings are filled in.
CREATE TABLE messenger_connection (
    page_id          text        PRIMARY KEY,
    business_id      uuid        NOT NULL REFERENCES business (id),
    page_name        text        NOT NULL,
    page_token_enc   text        NOT NULL,
    connected_at_utc timestamptz NOT NULL,
    CONSTRAINT messenger_connection_page_id CHECK (length(page_id) BETWEEN 1 AND 100),
    CONSTRAINT messenger_connection_page_name CHECK (length(page_name) BETWEEN 1 AND 200)
);
CREATE INDEX ix_messenger_connection_business ON messenger_connection (business_id);
