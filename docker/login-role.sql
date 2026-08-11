-- Creates one PostgreSQL LOGIN role, sets its password, and grants it membership in one of the
-- NOLOGIN group roles created by database/postgresql/roles/jobtrack-roles-and-grants.sql. Invoked
-- once per role by docker/provision.sh; see docs/operations/postgresql-cloud-run-deployment.md
-- §"Credential separation" for the four-way mapping this produces.
--
-- The repository holds no environment credentials (the roles script's own header says so), so this
-- takes its three inputs from the environment rather than carrying any:
--
--   JOBTRACK_ROLE_NAME      the LOGIN role to create, e.g. jobtrack_domain_login
--   JOBTRACK_GROUP_ROLE     the group role to grant it, e.g. jobtrack_domain
--   JOBTRACK_ROLE_PASSWORD  the generated password
--
-- \getenv rather than psql's -v: a password passed as -v lands in the process's argv, visible in the
-- container's process list, which is exactly the channel JobTrack.AdminCli and JobTrack.Database
-- refuse for the same reason (security review remediation §2.7).
--
-- format(%I/%L) rather than string concatenation, and \gexec rather than a DO block, because psql
-- performs no variable interpolation inside a dollar-quoted body -- a DO $$ ... $$ block cannot see
-- :'role_name' at all. \gexec also keeps the generated statement out of the output, so the password
-- is not echoed.
--
-- Idempotent: re-running resets the password to the current value, which is how a rotation is
-- applied, and re-grants a membership that is already held (a no-op).

\set QUIET on
\getenv role_name JOBTRACK_ROLE_NAME
\getenv group_role JOBTRACK_GROUP_ROLE
\getenv role_password JOBTRACK_ROLE_PASSWORD

SELECT format('CREATE ROLE %I NOLOGIN', :'role_name')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role_name')
\gexec

SELECT format('ALTER ROLE %I LOGIN PASSWORD %L', :'role_name', :'role_password')
\gexec

SELECT format('GRANT %I TO %I', :'group_role', :'role_name')
\gexec
