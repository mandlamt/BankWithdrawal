-- Minimal schema assumed by the repository implementations.
-- accounts.balance is never allowed to go negative because every write goes
-- through the conditional UPDATE in SqlAccountRepository.TryDebitAsync.

CREATE TABLE IF NOT EXISTS accounts (
    id       BIGINT PRIMARY KEY,
    balance  NUMERIC(19,4) NOT NULL CHECK (balance >= 0),
    currency CHAR(3) NOT NULL DEFAULT 'USD'
);

-- The outbox table is both the transactional-outbox queue AND the audit log:
-- every withdrawal attempt that reached the debit step leaves a permanent,
-- append-only record here, including its eventual publish status.
CREATE TABLE IF NOT EXISTS withdrawal_outbox (
    event_id         UUID PRIMARY KEY,
    account_id       BIGINT NOT NULL REFERENCES accounts(id),
    idempotency_key  TEXT NOT NULL,
    payload          JSONB NOT NULL,
    status           TEXT NOT NULL CHECK (status IN ('PENDING', 'PUBLISHED', 'DEAD_LETTER')),
    attempts         INT NOT NULL DEFAULT 0,
    created_at_utc   TIMESTAMPTZ NOT NULL,
    published_at_utc TIMESTAMPTZ,

    -- Enforces exactly-once processing of a given client-supplied idempotency key
    -- per account at the database level, not just in application code.
    UNIQUE (account_id, idempotency_key)
);

CREATE INDEX IF NOT EXISTS ix_withdrawal_outbox_pending
    ON withdrawal_outbox (created_at_utc)
    WHERE status = 'PENDING';
