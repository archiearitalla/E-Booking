-- VeskaFlow initial schema (PostgreSQL 13+).
-- Additive-only policy: never DROP/ALTER existing tables in later migrations without an explicit, reviewed plan.

CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE business (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    slug                text NOT NULL,
    name                text NOT NULL,
    time_zone           text NOT NULL DEFAULT 'Asia/Manila',     -- IANA id, never an offset
    slot_minutes        integer NOT NULL DEFAULT 30,
    lead_time_minutes   integer NOT NULL DEFAULT 60,
    horizon_days        integer NOT NULL DEFAULT 30,
    cancel_cutoff_hours integer NOT NULL DEFAULT 2,
    plan                text NOT NULL DEFAULT 'FREE',
    created_at_utc      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT business_slug_format CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND length(slug) BETWEEN 3 AND 40),
    CONSTRAINT business_slot_minutes CHECK (slot_minutes IN (5, 10, 15, 20, 30, 60)),
    CONSTRAINT business_lead_time CHECK (lead_time_minutes >= 0),
    CONSTRAINT business_horizon CHECK (horizon_days BETWEEN 1 AND 365),
    CONSTRAINT business_cancel_cutoff CHECK (cancel_cutoff_hours >= 0),
    CONSTRAINT business_plan CHECK (plan IN ('FREE', 'PRO'))
);
CREATE UNIQUE INDEX ux_business_slug ON business (slug);

CREATE TABLE admin_user (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id    uuid NOT NULL REFERENCES business (id),
    email          text NOT NULL,
    display_name   text NOT NULL,
    password_hash  text NOT NULL,
    role           text NOT NULL DEFAULT 'OWNER',
    is_active      boolean NOT NULL DEFAULT true,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT admin_user_role CHECK (role IN ('OWNER', 'MANAGER', 'STAFF'))
);
CREATE UNIQUE INDEX ux_admin_user_email ON admin_user (lower(email));
CREATE INDEX ix_admin_user_business ON admin_user (business_id);

-- (id, business_id) is unique so child tables can use composite FKs and can never point at another tenant's row.
CREATE TABLE staff (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id uuid NOT NULL REFERENCES business (id),
    name        text NOT NULL,
    is_active   boolean NOT NULL DEFAULT true,
    sort_order  integer NOT NULL DEFAULT 0,
    UNIQUE (id, business_id)
);
CREATE INDEX ix_staff_business ON staff (business_id);

CREATE TABLE service (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id      uuid NOT NULL REFERENCES business (id),
    name             text NOT NULL,
    duration_minutes integer NOT NULL,
    buffer_minutes   integer NOT NULL DEFAULT 0,
    price            numeric(10, 2) NOT NULL DEFAULT 0,
    is_active        boolean NOT NULL DEFAULT true,
    sort_order       integer NOT NULL DEFAULT 0,
    UNIQUE (id, business_id),
    CONSTRAINT service_duration CHECK (duration_minutes BETWEEN 5 AND 600),
    CONSTRAINT service_buffer CHECK (buffer_minutes BETWEEN 0 AND 240),
    CONSTRAINT service_price CHECK (price >= 0)
);
CREATE INDEX ix_service_business ON service (business_id);

CREATE TABLE staff_service (
    staff_id    uuid NOT NULL,
    service_id  uuid NOT NULL,
    business_id uuid NOT NULL,
    PRIMARY KEY (staff_id, service_id),
    FOREIGN KEY (staff_id, business_id) REFERENCES staff (id, business_id) ON DELETE CASCADE,
    FOREIGN KEY (service_id, business_id) REFERENCES service (id, business_id) ON DELETE CASCADE
);

-- One row per open window; several rows per day allowed (split shifts).
CREATE TABLE working_hour (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    staff_id    uuid NOT NULL REFERENCES staff (id) ON DELETE CASCADE,
    day_of_week smallint NOT NULL,                 -- 0 = Sunday ... 6 = Saturday (System.DayOfWeek)
    open_time   time NOT NULL,
    close_time  time NOT NULL,                     -- close <= open means the window crosses midnight
    CONSTRAINT working_hour_dow CHECK (day_of_week BETWEEN 0 AND 6),
    CONSTRAINT working_hour_not_empty CHECK (open_time <> close_time)
);
CREATE INDEX ix_working_hour_staff ON working_hour (staff_id, day_of_week);

CREATE TABLE blackout (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id    uuid NOT NULL REFERENCES business (id),
    staff_id       uuid NULL,                      -- NULL = whole shop closed
    starts_at_utc  timestamptz NOT NULL,
    ends_at_utc    timestamptz NOT NULL,
    reason         text NULL,
    FOREIGN KEY (staff_id, business_id) REFERENCES staff (id, business_id) ON DELETE CASCADE,
    CONSTRAINT blackout_range CHECK (ends_at_utc > starts_at_utc)
);
CREATE INDEX ix_blackout_business ON blackout (business_id, starts_at_utc);

CREATE TABLE customer (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id    uuid NOT NULL REFERENCES business (id),
    name           text NOT NULL,
    phone          text NOT NULL,
    email          text NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (id, business_id),
    UNIQUE (business_id, phone)
);

CREATE TABLE booking (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_id           uuid NOT NULL REFERENCES business (id),
    customer_id           uuid NOT NULL,
    staff_id              uuid NOT NULL,
    service_id            uuid NOT NULL,
    starts_at_utc         timestamptz NOT NULL,
    ends_at_utc           timestamptz NOT NULL,
    occupied_until_utc    timestamptz NOT NULL,    -- ends_at_utc + service buffer (computed by the app, snapshot)
    location_time_zone    text NOT NULL,
    status                text NOT NULL,
    hold_expires_at_utc   timestamptz NULL,
    price_snapshot        numeric(10, 2) NOT NULL,
    service_name_snapshot text NOT NULL,
    manage_token          text NOT NULL,           -- unguessable; the customer's only credential for self-service
    idempotency_key       text NULL,
    cancelled_by          text NULL,
    cancel_reason         text NULL,
    cancelled_at_utc      timestamptz NULL,
    created_at_utc        timestamptz NOT NULL DEFAULT now(),
    updated_at_utc        timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (customer_id, business_id) REFERENCES customer (id, business_id),
    FOREIGN KEY (staff_id, business_id)    REFERENCES staff (id, business_id),
    FOREIGN KEY (service_id, business_id)  REFERENCES service (id, business_id),
    CONSTRAINT booking_status CHECK (status IN ('HELD', 'PENDING', 'CONFIRMED', 'COMPLETED', 'CANCELLED', 'NO_SHOW')),
    CONSTRAINT booking_range CHECK (ends_at_utc > starts_at_utc AND occupied_until_utc >= ends_at_utc),
    -- Derived, never hand-maintained: only active bookings occupy time.
    active_range tstzrange GENERATED ALWAYS AS (
        CASE WHEN status IN ('HELD', 'PENDING', 'CONFIRMED')
             THEN tstzrange(starts_at_utc, occupied_until_utc, '[)')
        END
    ) STORED,
    -- Invariant #1: a staff member can never be double-booked. Enforced by the database at commit time.
    CONSTRAINT no_double_booking EXCLUDE USING gist (staff_id WITH =, active_range WITH &&)
);
CREATE UNIQUE INDEX ux_booking_manage_token ON booking (manage_token);
CREATE UNIQUE INDEX ux_booking_idempotency ON booking (business_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX ix_booking_business_start ON booking (business_id, starts_at_utc);
CREATE INDEX ix_booking_staff_start ON booking (staff_id, starts_at_utc);
CREATE INDEX ix_booking_hold_expiry ON booking (hold_expires_at_utc) WHERE status IN ('HELD', 'PENDING');

CREATE TABLE audit_log (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    business_id uuid NOT NULL REFERENCES business (id),
    booking_id  uuid NULL REFERENCES booking (id),
    actor_type  text NOT NULL,                     -- CUSTOMER | STAFF | SYSTEM
    actor_id    uuid NULL,
    actor_label text NOT NULL,
    action      text NOT NULL,
    from_status text NULL,
    to_status   text NULL,
    reason      text NULL,
    at_utc      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_business_time ON audit_log (business_id, at_utc DESC);
CREATE INDEX ix_audit_booking ON audit_log (booking_id);
