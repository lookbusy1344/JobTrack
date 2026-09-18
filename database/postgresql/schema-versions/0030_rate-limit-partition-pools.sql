-- Schema version 0030 (PostgreSQL): separate bounded primary and backstop rate-limit pools.
-- Capacity is a storage bound, not a deployment-wide denial policy: the SECURITY DEFINER consume
-- function evicts within the pressured pool while preserving the other pool. Existing rows predate
-- the discriminator and are conservatively retained as primary rows until their windows expire.

ALTER TABLE rate_limit_window
    ADD COLUMN partition_kind smallint NOT NULL DEFAULT 0,
    ADD CONSTRAINT rate_limit_window_partition_kind_check CHECK (partition_kind IN (0, 1));

ALTER TABLE rate_limit_window DROP CONSTRAINT rate_limit_window_pkey;
ALTER TABLE rate_limit_window
    ADD PRIMARY KEY (purpose, partition_kind, partition_digest, window_start);

CREATE INDEX rate_limit_window_pool_eviction_idx
    ON rate_limit_window (purpose, partition_kind, window_start, partition_digest);
