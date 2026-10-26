-- The shop's logo, kept in the database (the web host's disk is not kept between deploys). One small image per shop:
-- PNG, JPEG or WebP up to 256 KB. Additive only: one new table.

CREATE TABLE business_logo (
    business_id    uuid PRIMARY KEY REFERENCES business (id),
    content_type   text NOT NULL,
    data           bytea NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT business_logo_type CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    CONSTRAINT business_logo_size CHECK (octet_length(data) BETWEEN 1 AND 262144)
);
