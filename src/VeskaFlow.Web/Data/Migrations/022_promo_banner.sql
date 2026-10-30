-- A shop's promo banner for its public booking page, kept in the database like the logo (the web host's disk is not kept
-- between deploys). One picture per shop: PNG, JPEG or WebP up to 512 KB, with a short line saying what the promo says (used
-- as the picture's alt text), a switch, and an optional last day it shows (a date in the shop's own time zone).
-- Additive only: one new table.

CREATE TABLE business_banner (
    business_id    uuid PRIMARY KEY REFERENCES business (id),
    content_type   text        NOT NULL,
    data           bytea       NOT NULL,
    caption        text        NOT NULL,
    is_active      boolean     NOT NULL DEFAULT true,
    ends_on        date        NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT business_banner_type CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    CONSTRAINT business_banner_size CHECK (octet_length(data) BETWEEN 1 AND 524288),
    CONSTRAINT business_banner_caption CHECK (char_length(caption) BETWEEN 1 AND 100)
);
