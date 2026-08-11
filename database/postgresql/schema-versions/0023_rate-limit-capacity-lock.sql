-- Schema version 0023 (PostgreSQL): one durable lock row per rate-limit purpose. New partition
-- admission locks this row before counting/inserting live windows, making the configured capacity
-- bound exact across independent web hosts without serializing calls for already-known partitions.

CREATE TABLE rate_limit_capacity_lock
(
    purpose text PRIMARY KEY
);
