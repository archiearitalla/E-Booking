-- Pictures the platform owner keeps for the whole system, for example the GCash QR code shown to clients who need to pay.
-- Stored in the database (the web host's disk is not kept between deploys). Additive only: one new table.

CREATE TABLE platform_image (
    key            text PRIMARY KEY,
    content_type   text NOT NULL,
    data           bytea NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_image_key CHECK (length(key) BETWEEN 1 AND 60),
    CONSTRAINT platform_image_type CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    CONSTRAINT platform_image_size CHECK (octet_length(data) BETWEEN 1 AND 524288)
);
