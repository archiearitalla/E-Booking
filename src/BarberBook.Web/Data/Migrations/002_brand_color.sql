-- Per-shop accent color for the public booking pages. A preset key, never a free-form value,
-- so it can be applied through a CSS rule (the CSP forbids inline styles) and cannot carry markup.
-- Additive only: one new column with a default, so existing shops keep today's look.

ALTER TABLE business
    ADD COLUMN brand_color text NOT NULL DEFAULT 'teal';

ALTER TABLE business
    ADD CONSTRAINT business_brand_color CHECK (brand_color IN ('teal', 'blue', 'orange', 'rose', 'violet', 'slate'));
