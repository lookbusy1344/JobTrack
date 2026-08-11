-- SECURITY DEFINER functions narrowing runtime access to personal_access_token and (since ADR 0066
-- Stage 5) rate_limit_window -- two unrelated tables sharing this file only because both are
-- reached exclusively through a narrow function surface rather than a direct table grant, and both
-- are unversioned deployment-tool infrastructure re-applied after every schema deployment.
--
-- personal_access_token (security review remediation §2.6). jobtrack_domain
-- has no direct SELECT/INSERT/UPDATE grant on personal_access_token at all
-- (see ../roles/jobtrack-roles-and-grants.sql); the full issue/authenticate/
-- list/revoke/last-used-update lifecycle is exposed only through the narrow
-- function signatures below, owned by whichever role runs this script
-- (jobtrack_owner or a member, matching every other schema object -- see the
-- roles script's ownership comment). Lifecycle functions are EXECUTE-granted
-- only to jobtrack_pat_management; token lookup/last-used is granted only to
-- jobtrack_pat_authentication. jobtrack_domain retains revoke-all only because
-- credential/account transitions revoke PATs atomically with their audit row.
--
-- Deployment-tool infrastructure like ../roles/jobtrack-roles-and-grants.sql:
-- not schema-versioned, applied idempotently after every schema deployment,
-- after that roles script (their GRANT EXECUTE targets must already exist).
--
-- `SET search_path = public, pg_temp` on every function is deliberate:
-- SECURITY DEFINER functions run with the *owner's* privileges, so without a
-- pinned search_path a caller able to create objects earlier in an
-- attacker-controlled search_path could shadow an unqualified reference and
-- have it execute with the owner's privileges instead (CVE-class search-path
-- hijack). Every REVOKE ALL FROM PUBLIC below undoes PostgreSQL's default
-- "EXECUTE granted to PUBLIC on every new function" before the explicit,
-- capability-specific grants at the end of the script.

CREATE OR REPLACE FUNCTION pat_issue(
    p_app_user_id bigint,
    p_token_hash text,
    p_label text,
    p_created_at timestamptz,
    p_expires_at timestamptz
) RETURNS bigint
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
DECLARE
    v_id bigint;
BEGIN
    INSERT INTO personal_access_token (app_user_id, token_hash, label, created_at, expires_at)
    VALUES (p_app_user_id, p_token_hash, p_label, p_created_at, p_expires_at)
    RETURNING id INTO v_id;
    RETURN v_id;
END;
$$;

CREATE OR REPLACE FUNCTION pat_try_authenticate(
    p_token_hash text,
    p_now timestamptz
) RETURNS TABLE
          (
              id          bigint,
              app_user_id bigint
          )
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
UPDATE personal_access_token AS pat
SET last_used_at = p_now
FROM identity_user AS owner
WHERE pat.token_hash = p_token_hash
  AND pat.revoked_at IS NULL
  AND pat.expires_at > p_now
  AND owner.app_user_id = pat.app_user_id
  AND owner.is_enabled
  AND (NOT owner.lockout_enabled OR owner.lockout_end IS NULL OR owner.lockout_end <= p_now)
RETURNING pat.id, pat.app_user_id;
$$;

CREATE OR REPLACE FUNCTION pat_list(p_app_user_id bigint)
    RETURNS TABLE
            (
                id           bigint,
                label        text,
                created_at   timestamptz,
                expires_at   timestamptz,
                revoked_at   timestamptz,
                last_used_at timestamptz
            )
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
SELECT personal_access_token.id,
       personal_access_token.label,
       personal_access_token.created_at,
       personal_access_token.expires_at,
       personal_access_token.revoked_at,
       personal_access_token.last_used_at
FROM personal_access_token
WHERE personal_access_token.app_user_id = p_app_user_id
ORDER BY personal_access_token.created_at DESC;
$$;

-- found = false when no token with p_token_id exists for p_app_user_id (caller throws
-- EntityNotFoundException); found = true, newly_revoked = false when the token was already
-- revoked (caller is a no-op, matching the pre-function behaviour of only writing an audit
-- event when RevokedAt was previously null).
CREATE OR REPLACE FUNCTION pat_revoke(
    p_token_id bigint,
    p_app_user_id bigint,
    p_now timestamptz
) RETURNS TABLE
          (
              found         boolean,
              newly_revoked boolean
          )
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
DECLARE
    v_existing timestamptz;
BEGIN
    SELECT personal_access_token.revoked_at
    INTO v_existing
    FROM personal_access_token
    WHERE personal_access_token.id = p_token_id
      AND personal_access_token.app_user_id = p_app_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT false, false;
        RETURN;
    END IF;

    IF v_existing IS NULL THEN
        UPDATE personal_access_token SET revoked_at = p_now WHERE personal_access_token.id = p_token_id;
        RETURN QUERY SELECT true, true;
    ELSE
        RETURN QUERY SELECT true, false;
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION pat_revoke_all(
    p_app_user_id bigint,
    p_now timestamptz
) RETURNS integer
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
DECLARE
    v_count integer;
BEGIN
    UPDATE personal_access_token
    SET revoked_at = p_now
    WHERE app_user_id = p_app_user_id
      AND revoked_at IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

REVOKE ALL ON FUNCTION pat_issue(bigint, text, text, timestamptz, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION pat_try_authenticate(text, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION pat_list(bigint) FROM PUBLIC;
REVOKE ALL ON FUNCTION pat_revoke(bigint, bigint, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION pat_revoke_all(bigint, timestamptz) FROM PUBLIC;

REVOKE ALL ON FUNCTION pat_issue(bigint, text, text, timestamptz, timestamptz) FROM jobtrack_domain;
REVOKE ALL ON FUNCTION pat_try_authenticate(text, timestamptz) FROM jobtrack_domain;
REVOKE ALL ON FUNCTION pat_list(bigint) FROM jobtrack_domain;
REVOKE ALL ON FUNCTION pat_revoke(bigint, bigint, timestamptz) FROM jobtrack_domain;
REVOKE ALL ON FUNCTION pat_revoke_all(bigint, timestamptz) FROM jobtrack_domain;

GRANT EXECUTE ON FUNCTION pat_issue(bigint, text, text, timestamptz, timestamptz) TO jobtrack_pat_management;
GRANT EXECUTE ON FUNCTION pat_list(bigint) TO jobtrack_pat_management;
GRANT EXECUTE ON FUNCTION pat_revoke(bigint, bigint, timestamptz) TO jobtrack_pat_management;
GRANT EXECUTE ON FUNCTION pat_revoke_all(bigint, timestamptz) TO jobtrack_pat_management;
GRANT EXECUTE ON FUNCTION pat_revoke_all(bigint, timestamptz) TO jobtrack_domain;
GRANT EXECUTE ON FUNCTION pat_try_authenticate(text, timestamptz) TO jobtrack_pat_authentication;

-- rate_limit_try_consume: the shared PostgreSQL fixed-window rate-limit primitive (ADR 0066 Stage 5,
-- docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.4). Atomically evaluates and
-- consumes one or two partitions (a primary partition, and for the login limiter a coarser backstop
-- partition) in a single decision -- two independent increments would let a caller succeed on one
-- counter and fail the other, leaving a partial, unrecoverable decision. Rows are keyed by a
-- caller-supplied purpose plus a digest of the raw partition key (never the raw username/IP/PAT
-- identity itself). Both potential rows are locked in a fixed digest order regardless of which is
-- "primary" vs "backstop" for this call, so two concurrent calls naming the same pair of partitions
-- in opposite roles can never deadlock against each other. Expired rows for the calling purpose are
-- pruned via the window_start index on every call, bounding the table without a scheduled job or a
-- full scan; out_rows_pruned reports that call's count so RateLimitMetrics (JobTrack.Identity) can expose
-- it without a second round trip. p_backstop_digest/p_backstop_permit_limit are NULL for a caller
-- with no backstop partition (the external API limiter).
CREATE OR REPLACE FUNCTION rate_limit_try_consume(
    p_purpose text,
    p_partition_digest bytea,
    p_backstop_digest bytea,
    p_now timestamptz,
    p_window_seconds integer,
    p_permit_limit integer,
    p_backstop_permit_limit integer,
    p_max_partition_count integer,
    OUT out_allowed boolean,
    OUT out_rows_pruned integer
)
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
DECLARE
    v_window_start   timestamptz;
    v_primary_count  integer := 0;
    v_backstop_count integer := 0;
    v_live_count     integer;
    v_missing_count  integer;
    v_pruned_count   integer;
    v_row            record;
BEGIN
    IF p_max_partition_count <= 0 THEN
        RAISE EXCEPTION 'p_max_partition_count must be positive';
    END IF;

    v_window_start := to_timestamp(floor(extract(epoch FROM p_now) / p_window_seconds) * p_window_seconds);
    out_rows_pruned := 0;

    -- Prune before any denial path. In particular, an exhausted backstop must not bypass cleanup
    -- while a caller varies primary keys indefinitely.
    DELETE
    FROM rate_limit_window
    WHERE purpose = p_purpose
      AND window_start <= v_window_start - make_interval(secs => p_window_seconds);
    GET DIAGNOSTICS out_rows_pruned = ROW_COUNT;

    SELECT count(*)::integer
    INTO v_missing_count
    FROM (SELECT DISTINCT digest
          FROM unnest(ARRAY[p_partition_digest, p_backstop_digest]) AS requested(digest)
          WHERE digest IS NOT NULL) AS requested
    WHERE NOT EXISTS (
        SELECT 1
        FROM rate_limit_window
        WHERE purpose = p_purpose
          AND partition_digest = requested.digest
          AND window_start = v_window_start);

    IF v_missing_count > 0 THEN
        INSERT INTO rate_limit_capacity_lock (purpose)
        VALUES (p_purpose)
        ON CONFLICT (purpose) DO NOTHING;

        PERFORM 1
        FROM rate_limit_capacity_lock
        WHERE purpose = p_purpose
        FOR UPDATE;

        -- A concurrent creator may have waited on the same purpose lock. Re-prune and re-evaluate
        -- after acquiring it so the capacity decision observes that transaction's committed rows.
        DELETE
        FROM rate_limit_window
        WHERE purpose = p_purpose
          AND window_start <= v_window_start - make_interval(secs => p_window_seconds);
        GET DIAGNOSTICS v_pruned_count = ROW_COUNT;
        out_rows_pruned := out_rows_pruned + v_pruned_count;

        SELECT count(*)::integer
        INTO v_missing_count
        FROM (SELECT DISTINCT digest
              FROM unnest(ARRAY[p_partition_digest, p_backstop_digest]) AS requested(digest)
              WHERE digest IS NOT NULL) AS requested
        WHERE NOT EXISTS (
            SELECT 1
            FROM rate_limit_window
            WHERE purpose = p_purpose
              AND partition_digest = requested.digest
              AND window_start = v_window_start);

        SELECT count(*)::integer
        INTO v_live_count
        FROM rate_limit_window
        WHERE purpose = p_purpose;

        IF v_live_count + v_missing_count > p_max_partition_count THEN
            out_allowed := false;
            RETURN;
        END IF;

        INSERT INTO rate_limit_window (purpose, partition_digest, window_start, permit_count)
        SELECT p_purpose, digest, v_window_start, 0
        FROM (SELECT DISTINCT digest
              FROM unnest(ARRAY[p_partition_digest, p_backstop_digest]) AS requested(digest)
              WHERE digest IS NOT NULL) AS requested
        ORDER BY digest
        ON CONFLICT (purpose, partition_digest, window_start) DO NOTHING;
    END IF;

    FOR v_row IN
        SELECT partition_digest, permit_count
        FROM rate_limit_window
        WHERE purpose = p_purpose
          AND window_start = v_window_start
          AND partition_digest IN (p_partition_digest, p_backstop_digest)
        ORDER BY partition_digest
            FOR UPDATE
        LOOP
            IF v_row.partition_digest = p_partition_digest THEN
                v_primary_count := v_row.permit_count;
            ELSE
                v_backstop_count := v_row.permit_count;
            END IF;
        END LOOP;

    IF v_primary_count >= p_permit_limit
        OR (p_backstop_digest IS NOT NULL AND v_backstop_count >= p_backstop_permit_limit) THEN
        out_allowed := false;
        RETURN;
    END IF;

    UPDATE rate_limit_window
    SET permit_count = permit_count + 1
    WHERE purpose = p_purpose
      AND partition_digest = p_partition_digest
      AND window_start = v_window_start;

    IF p_backstop_digest IS NOT NULL THEN
        UPDATE rate_limit_window
        SET permit_count = permit_count + 1
        WHERE purpose = p_purpose
          AND partition_digest = p_backstop_digest
          AND window_start = v_window_start;
    END IF;

    out_allowed := true;
END;
$$;

-- Rolling-revision compatibility: preceding web revisions call the seven-argument signature. Keep
-- it as a bounded wrapper until every such revision has been retired; a later contract migration
-- may remove it.
CREATE OR REPLACE FUNCTION rate_limit_default_max_partition_count() RETURNS integer
    LANGUAGE sql
    IMMUTABLE
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
SELECT 4096;
$$;

CREATE OR REPLACE FUNCTION rate_limit_try_consume(
    p_purpose text,
    p_partition_digest bytea,
    p_backstop_digest bytea,
    p_now timestamptz,
    p_window_seconds integer,
    p_permit_limit integer,
    p_backstop_permit_limit integer,
    OUT out_allowed boolean,
    OUT out_rows_pruned integer
)
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
SELECT out_allowed, out_rows_pruned
FROM rate_limit_try_consume(
    p_purpose, p_partition_digest, p_backstop_digest, p_now, p_window_seconds,
    p_permit_limit, p_backstop_permit_limit, rate_limit_default_max_partition_count());
$$;

REVOKE ALL ON FUNCTION rate_limit_default_max_partition_count() FROM PUBLIC;
REVOKE ALL ON FUNCTION rate_limit_try_consume(text, bytea, bytea, timestamptz, integer, integer, integer, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION rate_limit_try_consume(text, bytea, bytea, timestamptz, integer, integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION rate_limit_try_consume(text, bytea, bytea, timestamptz, integer, integer, integer, integer) TO jobtrack_identity;
GRANT EXECUTE ON FUNCTION rate_limit_try_consume(text, bytea, bytea, timestamptz, integer, integer, integer) TO jobtrack_identity;

-- rate_limit_live_partition_count: an approximate, catalog-only row-count estimate (pg_class.reltuples,
-- refreshed by autovacuum/ANALYZE) for the "live partition count" operational metric (plan §2.4) --
-- deliberately not an exact SELECT COUNT(*), which would be an unbounded scan on every observation of
-- an ObservableGauge (RateLimitMetrics, JobTrack.Web). Good enough for an alerting/dashboard signal,
-- not for correctness decisions.
CREATE OR REPLACE FUNCTION rate_limit_live_partition_count() RETURNS real
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS
$$
SELECT reltuples
FROM pg_class
WHERE relname = 'rate_limit_window';
$$;

REVOKE ALL ON FUNCTION rate_limit_live_partition_count() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION rate_limit_live_partition_count() TO jobtrack_identity;
