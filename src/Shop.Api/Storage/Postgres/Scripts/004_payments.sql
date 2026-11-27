-- Spec 005 data-model.md "Shop app": payment state on orders, recorded transfers, chain progress.

-- Order (extends 003). Existing orders keep paying_account NULL (they can never match) and get the
-- default 30-minute deadline from their creation time.
ALTER TABLE orders
    ADD COLUMN paying_account   text,
    ADD COLUMN deadline         timestamptz,
    ADD COLUMN total_base_units numeric(30, 0) GENERATED ALWAYS AS (total_cents::numeric * 10000) STORED,
    ADD COLUMN paid_tx          text,
    ADD COLUMN paid_log_index   integer,
    ADD COLUMN paid_block       bigint,
    ADD COLUMN paid_at          timestamptz;

UPDATE orders SET deadline = created_at + interval '30 minutes';

ALTER TABLE orders
    ALTER COLUMN deadline SET NOT NULL,
    ADD CONSTRAINT orders_status_check CHECK (status IN ('PendingPayment', 'Paid', 'Expired')),
    ADD CONSTRAINT orders_paid_check CHECK ((status = 'Paid') = (paid_tx IS NOT NULL));

-- FR-005a: at most one order waiting for payment per paying account.
CREATE UNIQUE INDEX orders_one_waiting_per_account ON orders (paying_account) WHERE status = 'PendingPayment';

-- Matching (by account) and the expiry job (waiting, by deadline).
CREATE INDEX orders_account_created_idx ON orders (paying_account, created_at DESC);
CREATE INDEX orders_waiting_deadline_idx ON orders (deadline) WHERE status = 'PendingPayment';
CREATE INDEX orders_wallet_idx ON orders (wallet_address);

-- Payment: every delivered transfer to one of this shop's addresses; receipt is idempotent.
CREATE TABLE payments (
    tx_hash          text           NOT NULL,
    log_index        integer        NOT NULL,
    block            bigint         NOT NULL,
    block_time       timestamptz    NOT NULL,
    from_account     text           NOT NULL,
    to_address       text           NOT NULL,
    value_base_units numeric(78, 0) NOT NULL CHECK (value_base_units >= 0),
    order_reference  text           REFERENCES orders (reference),
    unmatched_reason text           CHECK (unmatched_reason IN ('amount', 'account', 'late', 'duplicate', 'unknown')),
    received_at      timestamptz    NOT NULL,
    PRIMARY KEY (tx_hash, log_index),
    CHECK ((order_reference IS NULL) <> (unmatched_reason IS NULL))
);

CREATE INDEX payments_block_time_idx ON payments (block_time DESC, tx_hash DESC, log_index DESC);
CREATE UNIQUE INDEX payments_one_per_order ON payments (order_reference) WHERE order_reference IS NOT NULL;

-- Chain progress per receiving address; only moves forward.
CREATE TABLE chain_progress (
    address            text        PRIMARY KEY,
    seen_through_block bigint      NOT NULL,
    seen_through_time  timestamptz NOT NULL
);
