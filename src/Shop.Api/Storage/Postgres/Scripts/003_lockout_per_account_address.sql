-- Spec 004 FR-023: lockout per (account, client address), plus the shop-wide row ('*', '*').
-- The admin table's failed_count, failed_window_start and locked_until stop being used.
CREATE TABLE admin_lockouts (
    account             text        NOT NULL,
    address             text        NOT NULL,
    failed_count        integer     NOT NULL DEFAULT 0,
    failed_window_start timestamptz,
    locked_until        timestamptz,
    PRIMARY KEY (account, address)
);
