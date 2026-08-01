-- SECURITY DEFINER functions narrowing runtime access to
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
