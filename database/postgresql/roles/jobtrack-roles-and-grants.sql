-- PostgreSQL database roles and privilege separation (impl plan §6.1,
-- §6.7 gate item "role grants prove the normal application role cannot
-- perform DDL, erase audit rows, or delete retained history").
--
-- This is deployment-tool infrastructure, not a schema-versions script
-- (see 0012_audit-event.sql's header comment): it is not tracked in
-- schema_version and carries no version number. It is idempotent and
-- re-applied after every successful schema deployment on PostgreSQL, so
-- grants stay in sync as tables are added across schema versions.
--
-- Five roles, from least to most privileged. All are NOLOGIN group roles;
-- an actual login account for a deployment environment is created
-- separately (outside this repository, which holds no environment
-- credentials) and granted membership in the appropriate role below.
--   jobtrack_readonly        -- SELECT only, for reporting/auditors.
--   jobtrack_application     -- the running web/CLI app's runtime identity.
--   jobtrack_emergency_reset -- narrowly scoped credential-reset path (spec §8.6).
--   jobtrack_schema_deployer -- runs schema-versions scripts (inherits DDL
--                                rights via jobtrack_owner membership).
--   jobtrack_owner           -- owns every schema object.
--
-- Role creation is guarded by an exception handler rather than a
-- check-then-create, because pg_roles is a cluster-wide catalog shared by
-- every disposable per-test-class database on the same instance (§6.6):
-- two concurrent deployments provisioning the same database-less roles
-- for the first time would otherwise race on a plain "IF NOT EXISTS"
-- check (the same TOCTOU shape already proven elsewhere in this schema,
-- e.g. ADR 0012's cycle-prevention races), where PostgreSQL's own
-- duplicate-object detection is not.
DO $$
BEGIN
    BEGIN
        CREATE ROLE jobtrack_owner NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
    BEGIN
        CREATE ROLE jobtrack_schema_deployer NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
    BEGIN
        CREATE ROLE jobtrack_application NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
    BEGIN
        CREATE ROLE jobtrack_readonly NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
    BEGIN
        CREATE ROLE jobtrack_emergency_reset NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
END
$$;

-- jobtrack_schema_deployer inherits jobtrack_owner's ownership-based DDL
-- rights, so a login account granted jobtrack_schema_deployer can deploy
-- schema versions without ever being the literal object owner itself.
GRANT jobtrack_owner TO jobtrack_schema_deployer;

-- Reassigning existing objects to jobtrack_owner (REASSIGN OWNED BY
-- CURRENT_USER TO jobtrack_owner) is deliberately not automated here: when
-- the connecting role is itself a superuser that also owns
-- system-required objects (e.g. the local/test/CI admin role, which also
-- owns the database and cluster-wide catalog entries), PostgreSQL rejects
-- the reassignment with "cannot reassign ownership of objects owned by
-- role ... because they are required by the database system" (2BP01) --
-- REASSIGN OWNED has no way to scope itself to "just this database's
-- application tables". A provisioned production environment instead
-- creates jobtrack_owner first and runs schema deployment as (or as a
-- member of) jobtrack_schema_deployer from the start, so schema objects
-- are owned by jobtrack_owner from creation and no reassignment is ever
-- needed. Local/test/CI environments, which deploy as a superuser, rely
-- entirely on the explicit GRANTs below rather than on ownership.
--
-- PostgreSQL 15+ no longer grants CREATE on the public schema to PUBLIC by
-- default; this REVOKE is explicit, idempotent, defense-in-depth for
-- pre-15 instances rather than a behaviour change.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

GRANT USAGE ON SCHEMA public TO jobtrack_application, jobtrack_readonly, jobtrack_emergency_reset;

-- jobtrack_readonly: SELECT on every current table, re-granted each time
-- this script re-runs after a schema deployment, plus ALTER DEFAULT
-- PRIVILEGES for the current session's role as a defense-in-depth gap
-- filler between deployments within the same environment.
GRANT SELECT ON ALL TABLES IN SCHEMA public TO jobtrack_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO jobtrack_readonly;

-- Identity secret columns are never exposed to the ordinary reporting/
-- auditor path (threat model row 11, TC-DB-ROLES-002): reporting has no
-- legitimate reason to read a password hash or session-revocation stamp,
-- unlike jobtrack_application, which needs them for ASP.NET Core
-- Identity's own authentication/password-change flows. A column-level
-- REVOKE alone is not sufficient here: PostgreSQL still permits selecting
-- a column covered by a broader table-level GRANT, so the table-level
-- grant on identity_user must be replaced with an explicit column list.
-- two_factor_enabled/two_factor_enabled_at (ADR 0037) are account-state flags, not secrets --
-- exposed alongside lockout_enabled/access_failed_count. authenticator_key_protected stays
-- excluded: it is the encrypted TOTP shared secret, the same sensitivity class as password_hash.
REVOKE SELECT ON identity_user FROM jobtrack_readonly;
GRANT SELECT
    (id, app_user_id, user_name, normalized_user_name,
     requires_password_change, is_enabled, lockout_enabled, lockout_end, access_failed_count,
     two_factor_enabled, two_factor_enabled_at)
    ON identity_user TO jobtrack_readonly;

-- personal_access_token.token_hash is a credential-equivalent secret (ADR 0029) -- the same
-- reasoning as identity_user's column-level restriction above applies here (security review
-- remediation §2.7): reporting has no legitimate reason to read a token's hash, only its
-- non-secret metadata.
REVOKE SELECT ON personal_access_token FROM jobtrack_readonly;
GRANT SELECT
    (id, app_user_id, label, created_at, expires_at, revoked_at, last_used_at)
    ON personal_access_token TO jobtrack_readonly;

-- jobtrack_application: ordinary CRUD on current-state tables, no DDL
-- (never granted CREATE/ownership), and no DELETE on retained-history or
-- audit tables (spec §16, plan §2 "retain completed and cost-relevant
-- history; use archival rather than deletion"). audit_event additionally
-- has no UPDATE, matching its own append-only triggers as defense in
-- depth (see 0012_audit-event.sql).
GRANT SELECT ON
    achievement_status, priority, schedule_exception_effect, identity_role
    TO jobtrack_application, jobtrack_emergency_reset;

GRANT SELECT, INSERT ON initialised_marker TO jobtrack_application;

GRANT SELECT, INSERT, UPDATE ON app_user TO jobtrack_application;
GRANT SELECT, UPDATE ON app_user TO jobtrack_emergency_reset;

GRANT SELECT, INSERT, UPDATE ON identity_user TO jobtrack_application;
GRANT SELECT, UPDATE ON identity_user TO jobtrack_emergency_reset;
GRANT SELECT, INSERT, DELETE ON identity_user_role TO jobtrack_application;

GRANT SELECT, INSERT, UPDATE, DELETE ON
    job_node, leaf_work,
    user_schedule_version, user_schedule_interval, user_schedule_exception,
    user_cost_rate, node_rate_override
    TO jobtrack_application;

GRANT SELECT, INSERT, DELETE ON job_prerequisite TO jobtrack_application;

-- work_session: cost-relevant execution history -- corrected, never
-- deleted (spec: "audited correction").
GRANT SELECT, INSERT, UPDATE ON work_session TO jobtrack_application;

-- personal_access_token (ADR 0029): jobtrack_application owns the full
-- issue/list/revoke/last-used-update lifecycle. jobtrack_emergency_reset
-- may only revoke -- its emergency password reset also revokes every live
-- token (ADR 0029), so it gets only the non-secret columns needed to scope
-- revocation plus UPDATE on revoked_at. It never reads token_hash and never
-- mutates token-bearing metadata.
GRANT SELECT, INSERT, UPDATE ON personal_access_token TO jobtrack_application;
REVOKE SELECT, UPDATE ON personal_access_token FROM jobtrack_emergency_reset;
GRANT SELECT (id, app_user_id, revoked_at) ON personal_access_token TO jobtrack_emergency_reset;
GRANT UPDATE (revoked_at) ON personal_access_token TO jobtrack_emergency_reset;

-- audit_event: append-only to every normal role, including the
-- application role that writes it (spec §16).
GRANT SELECT, INSERT ON audit_event TO jobtrack_application, jobtrack_emergency_reset;
