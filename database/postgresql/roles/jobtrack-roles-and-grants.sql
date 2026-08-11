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
-- Eight roles, from least to most privileged. All are NOLOGIN group roles;
-- an actual login account for a deployment environment is created
-- separately (outside this repository, which holds no environment
-- credentials) and granted membership in the appropriate role below.
--   jobtrack_readonly        -- SELECT only, for reporting/auditors.
--   jobtrack_domain          -- the running web/CLI app's runtime identity for
--                                domain data, actor operations, and audit
--                                writes (IJobTrackClient). Split from ASP.NET
--                                Core Identity's own sign-in path (security
--                                review remediation §2.6) so a compromised
--                                credential on one side does not automatically
--                                carry the other's blast radius; has no direct
--                                PAT authority beyond revoke-all (needed by
--                                atomic credential transitions) and, as a
--                                documented residual, still shares
--                                identity_user secret-column access with
--                                jobtrack_identity because password-reset/2FA-
--                                reset/enable-disable command ports write
--                                those columns inside the same ACID
--                                transaction as their audit row.
--   jobtrack_identity        -- ASP.NET Core Identity's own sign-in path
--                                (JobTrackIdentityDbContext only): identity_user,
--                                identity_user_role, identity_role.
--   jobtrack_pat_authentication -- bearer-token lookup/last-used only.
--   jobtrack_pat_management  -- self-service/admin PAT lifecycle and its audit rows.
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
        CREATE ROLE jobtrack_domain NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
    BEGIN
        CREATE ROLE jobtrack_identity NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
    BEGIN
        CREATE ROLE jobtrack_pat_authentication NOLOGIN;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END;
    BEGIN
        CREATE ROLE jobtrack_pat_management NOLOGIN;
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

GRANT USAGE ON SCHEMA public TO
    jobtrack_domain, jobtrack_identity, jobtrack_pat_authentication, jobtrack_pat_management,
    jobtrack_readonly, jobtrack_emergency_reset;

-- jobtrack_readonly: SELECT on every current table, re-granted each time
-- this script re-runs after a schema deployment, plus ALTER DEFAULT
-- PRIVILEGES for the current session's role as a defense-in-depth gap
-- filler between deployments within the same environment.
GRANT SELECT ON ALL TABLES IN SCHEMA public TO jobtrack_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO jobtrack_readonly;

-- Identity secret columns are never exposed to the ordinary reporting/
-- auditor path (threat model row 11, TC-DB-ROLES-002): reporting has no
-- legitimate reason to read a password hash or session-revocation stamp,
-- unlike jobtrack_domain/jobtrack_identity, which need them for ASP.NET
-- Core Identity's own authentication/password-change flows. A column-level
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

-- jobtrack_domain/jobtrack_identity: ordinary CRUD on current-state tables,
-- no DDL (never granted CREATE/ownership), and no DELETE on retained-history
-- or audit tables (spec §16, plan §2 "retain completed and cost-relevant
-- history; use archival rather than deletion"). audit_event additionally
-- has no UPDATE, matching its own append-only triggers as defense in
-- depth (see 0012_audit-event.sql).
GRANT SELECT ON
    achievement_status, priority, schedule_exception_effect
    TO jobtrack_domain, jobtrack_emergency_reset;

-- identity_role is read by both the domain connection (role-name lookups
-- alongside identity_user_role) and jobtrack_identity's own sign-in path.
GRANT SELECT ON identity_role TO jobtrack_domain, jobtrack_identity, jobtrack_emergency_reset;

GRANT SELECT, INSERT ON initialised_marker TO jobtrack_domain;

GRANT SELECT, INSERT, UPDATE ON app_user TO jobtrack_domain;
GRANT SELECT, UPDATE ON app_user TO jobtrack_emergency_reset;

-- identity_user/identity_user_role (security review remediation §2.6,
-- documented residual): jobtrack_identity is ASP.NET Core Identity's own
-- sign-in path; jobtrack_domain also keeps full access because command
-- ports (password reset, 2FA reset, enable/disable, role assignment) write
-- these columns inside the same ACID transaction as their audit row (impl
-- plan §7.3, CLAUDE.md "compound writes are single ACID transactions") --
-- splitting those writes into a further SECURITY DEFINER boundary is left
-- as explicit follow-up work, not silently dropped (remediation item 6).
GRANT SELECT, INSERT, UPDATE ON identity_user TO jobtrack_domain, jobtrack_identity;

-- data_protection_key (schema version 0021, ADR 0066 Stage 2): the multi-instance PostgreSQL key
-- repository. jobtrack_identity is the only role that ever needs it -- it is
-- JobTrackIdentityDbContext's own table, read/written by ASP.NET Core Data Protection's EF Core
-- repository (PersistKeysToDbContext<PostgreSqlJobTrackIdentityDbContext>), never by application
-- code directly. No DELETE: key revocation adds a revocation element to existing XML rather than
-- removing rows.
GRANT SELECT, INSERT ON data_protection_key TO jobtrack_identity;
GRANT SELECT, UPDATE ON identity_user TO jobtrack_emergency_reset;
GRANT SELECT, INSERT, DELETE ON identity_user_role TO jobtrack_domain;
GRANT SELECT ON identity_user_role TO jobtrack_identity;

-- PAT management authenticates and authorizes the actor against current Identity state before
-- calling the narrow lifecycle functions, then appends the matching audit row in the same
-- transaction. It may read Identity state/roles but cannot change either. The bearer-authentication
-- role needs no table grant: pat_try_authenticate performs its enabled/lockout check inside the
-- SECURITY DEFINER function and returns only the non-secret token/user identifiers.
REVOKE SELECT ON identity_user FROM jobtrack_pat_management;
GRANT SELECT (id, app_user_id, is_enabled, lockout_enabled, lockout_end)
    ON identity_user TO jobtrack_pat_management;
GRANT SELECT ON identity_user_role, identity_role TO jobtrack_pat_management;
GRANT SELECT, INSERT ON audit_event TO jobtrack_pat_management;

GRANT SELECT, INSERT, UPDATE, DELETE ON
    job_node, leaf_work,
    user_schedule_version, user_schedule_interval, user_schedule_exception,
    user_cost_rate, node_rate_override
    TO jobtrack_domain;

GRANT SELECT, INSERT, DELETE ON job_prerequisite TO jobtrack_domain;

-- Requester intake, schema version 0020 (ADR 0033/0034). These grants were
-- missing when 0020 landed, which nothing caught until the first real
-- PostgreSQL deployment: SQLite has no roles, so the SQLite host stayed
-- healthy while every PostgreSQL page touching a request failed with
-- "permission denied for table job_request". PostgreSqlRoleGrantsTests'
-- The_domain_role_can_read_every_application_table now enumerates the live
-- catalog, so a table added later cannot repeat this.
--
--   job_request      -- submitted, then acknowledged in place
--                       (acknowledged_at/acknowledged_by_user_id). Never
--                       deleted: a withdrawn or rejected request stays as
--                       intake history, the same retention rule work_session
--                       follows above.
--   job_request_note -- append-only correspondence on a request.
--   department, app_user_department, request_holding_area -- read-only to the
--                       application. They are routing configuration, and no
--                       code path in src/ writes them; provisioning them is an
--                       administrative act outside the running application, so
--                       granting writes here would widen the domain role for a
--                       capability it does not exercise.
GRANT SELECT, INSERT, UPDATE ON job_request TO jobtrack_domain;
GRANT SELECT, INSERT ON job_request_note TO jobtrack_domain;
GRANT SELECT ON department, app_user_department, request_holding_area TO jobtrack_domain;

-- work_session: cost-relevant execution history -- corrected, never
-- deleted (spec: "audited correction").
GRANT SELECT, INSERT, UPDATE ON work_session TO jobtrack_domain;

-- personal_access_token (ADR 0029, security review remediation §2.6):
-- jobtrack_domain has NO direct table grant here at all -- a compromised
-- domain credential can no longer read token_hash for offline replay, or
-- forge/extend/revoke a token outside the narrow function shapes below.
-- The full issue/authenticate/list/revoke/last-used-update lifecycle is
-- exposed only through the SECURITY DEFINER functions in
-- database/postgresql/functions/, EXECUTE-granted to jobtrack_domain
-- there. jobtrack_emergency_reset keeps its pre-existing narrow
-- column-level revoke-only access unchanged -- its emergency password
-- reset also revokes every live token (ADR 0029), so it gets only the
-- non-secret columns needed to scope revocation plus UPDATE on
-- revoked_at. It never reads token_hash and never mutates token-bearing
-- metadata.
REVOKE ALL ON personal_access_token FROM jobtrack_emergency_reset;
GRANT SELECT (id, app_user_id, revoked_at) ON personal_access_token TO jobtrack_emergency_reset;
GRANT UPDATE (revoked_at) ON personal_access_token TO jobtrack_emergency_reset;

-- audit_event: append-only to every normal role, including the domain role
-- that writes it (spec §16) and reads it back for the administrator
-- audit-history view (PostgreSqlAuditQueryPort). Left as direct table
-- grants rather than a SECURITY DEFINER function in this remediation slice
-- (documented residual, remediation item 6) -- narrowing this further is
-- explicit follow-up work, not silently dropped.
GRANT SELECT, INSERT ON audit_event TO jobtrack_domain, jobtrack_emergency_reset;
