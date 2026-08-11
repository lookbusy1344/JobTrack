-- Schema version 0022 (PostgreSQL): rate_limit_window, the shared fixed-window rate-limit primitive
-- (multi-instance plan, ADR 0066 Stage 5, docs/plans/2026-07-26-multi-instance-web-deployment-plan.md
-- §2.4). Replaces the in-process login/API limiters' per-instance counters for multi-instance
-- deployments so every host enforces one true global limit. No application role has a direct grant on
-- this table -- it is reached only through the SECURITY DEFINER rate_limit_try_consume function in
-- database/postgresql/functions/jobtrack-security-definer-functions.sql, matching
-- personal_access_token's own access boundary.

CREATE TABLE rate_limit_window
(
    purpose          text        NOT NULL,
    partition_digest bytea       NOT NULL,
    window_start     timestamptz NOT NULL,
    permit_count     integer     NOT NULL DEFAULT 0,
    PRIMARY KEY (purpose, partition_digest, window_start)
);

-- Bounded expiry pruning (plan §2.4: "pruned without a table scan") reads this index, not the
-- primary key -- window_start is the PK's trailing column, so a "delete everything before X"
-- predicate on the PK alone cannot use it as a sargable range scan.
CREATE INDEX rate_limit_window_window_start_idx ON rate_limit_window (window_start);
