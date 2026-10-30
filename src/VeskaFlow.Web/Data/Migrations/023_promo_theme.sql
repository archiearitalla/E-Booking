-- Promo Creator: banner modes (halloween, christmas, ...), a sub-line, a big badge, and words-only banners.
--   business_banner.theme   the look of the promo, one of a closed list (the page styles each one; there is no free colour).
--   details                 an optional second line under the headline (up to 140 characters).
--   badge                   an optional short, big label such as "20% OFF" (up to 16 characters).
--   content_type, data      a banner may now be words only, so these two may be empty. This only relaxes NOT NULL: no row and no
--                           value is changed or removed. The two columns stay both empty or both filled.
-- The existing size and type checks pass for an empty value, so they stay as they are.

ALTER TABLE business_banner ADD COLUMN theme text NOT NULL DEFAULT 'plain';
ALTER TABLE business_banner ADD CONSTRAINT business_banner_theme CHECK (theme IN
    ('plain', 'halloween', 'christmas', 'new_year', 'valentines', 'summer', 'back_to_school', 'payday', 'rainy'));

ALTER TABLE business_banner ADD COLUMN details text NULL;
ALTER TABLE business_banner ADD COLUMN badge text NULL;
ALTER TABLE business_banner ADD CONSTRAINT business_banner_details CHECK (details IS NULL OR char_length(details) BETWEEN 1 AND 140);
ALTER TABLE business_banner ADD CONSTRAINT business_banner_badge CHECK (badge IS NULL OR char_length(badge) BETWEEN 1 AND 16);

ALTER TABLE business_banner ALTER COLUMN content_type DROP NOT NULL;
ALTER TABLE business_banner ALTER COLUMN data DROP NOT NULL;
ALTER TABLE business_banner ADD CONSTRAINT business_banner_picture_pair CHECK ((content_type IS NULL) = (data IS NULL));
