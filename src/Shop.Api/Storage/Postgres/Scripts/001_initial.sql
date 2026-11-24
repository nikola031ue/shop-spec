-- Spec 003 data-model.md "PostgreSQL layout".
CREATE TABLE items (
    id          uuid        PRIMARY KEY,
    name        text        NOT NULL,
    name_key    text        NOT NULL UNIQUE,
    stock       integer     NOT NULL CHECK (stock >= 0),
    price_cents bigint      NOT NULL CHECK (price_cents > 0),
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL
);

CREATE TABLE orders (
    reference      text        PRIMARY KEY,
    created_at     timestamptz NOT NULL,
    status         text        NOT NULL,
    wallet_address text        NOT NULL,
    total_cents    bigint      NOT NULL,
    lines          jsonb       NOT NULL
);

CREATE INDEX orders_created_at_idx ON orders (created_at DESC, reference DESC);

CREATE TABLE admin (
    id                  smallint    PRIMARY KEY CHECK (id = 1),
    password_hash       text        NOT NULL,
    initial             boolean     NOT NULL,
    changed_at          timestamptz,
    failed_count        integer     NOT NULL DEFAULT 0,
    failed_window_start timestamptz,
    locked_until        timestamptz
);

CREATE TABLE dp_keys (
    name text PRIMARY KEY,
    xml  text NOT NULL
);
