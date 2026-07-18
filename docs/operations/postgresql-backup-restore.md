# PostgreSQL backup and restore

**Closes:** Implementation plan §6.7 gate item "a schema-level PostgreSQL backup/restore smoke test
passes". This document describes what the automated smoke test proves, and the manual procedure it
is modelled on. It deliberately stops short of a production-like recovery-objective (RPO/RTO)
rehearsal — plan §6.7 assigns that to the release gate, not the database gate.

## What the automated smoke test proves

`PostgreSqlBackupRestoreTests.A_deployed_database_survives_a_pg_dump_and_pg_restore_round_trip`
(`TC-DB-BACKUP-001`, `tests/JobTrack.Database.ContractTests/PostgreSqlBackupRestoreTests.cs`):

1. Deploys every schema-version script and the roles-and-grants script (§6.1) to a disposable
   PostgreSQL database, then inserts one `app_user` row.
2. Runs `pg_dump --format=custom` against that database.
3. Runs `pg_restore` of the resulting archive into a second, empty disposable database.
4. Asserts the restored database has: the same `schema_version` row count, the same
   `achievement_status` reference data, the seeded `app_user` row, and — critically —
   that the `jobtrack_application` role is still blocked from `CREATE TABLE` (`SQLSTATE 42501`)
   after restore.

That last assertion is the reason this is a *schema-level* smoke test rather than a plain data
round-trip: `pg_dump` captures `GRANT`/`REVOKE` statements, and the test proves those statements
still resolve correctly once replayed into a fresh database, not just that tables and rows come
back.

`CREATE ROLE` is cluster-scoped, not per-database, so a single-database `pg_dump` never carries role
*definitions* — only the grants referencing them. The smoke test's target database is on the same
PostgreSQL instance as the source, where `jobtrack_application` etc. already exist (every fixture in
this project applies the roles-and-grants script the same way), so this is not a gap in the test;
see the manual procedure below for provisioning roles on a cluster that does not yet have them.

## Manual procedure

Tooling: `pg_dump` / `pg_restore` from a PostgreSQL client matching (or newer than) the server's
major version.

**Provisioning roles on a new cluster.** Run `database/postgresql/roles/jobtrack-roles-and-grants.sql`
once per cluster — it is idempotent (§6.1) and creates the five roles before any database restore
that references them in `GRANT` statements needs them to exist.

**Backup:**

```sh
pg_dump --format=custom --file=jobtrack-backup.dump "<connection string>"
```

Custom format (`-Fc`) is used rather than plain SQL so `pg_restore` can select objects and report
per-statement errors rather than aborting a single monolithic script.

**Restore**, into an existing empty database:

```sh
psql --dbname=postgres --command="CREATE DATABASE jobtrack_restored OWNER \"$(whoami)\" LOCALE_PROVIDER icu ICU_LOCALE 'en-GB' TEMPLATE template0"
pg_restore --dbname=jobtrack_restored jobtrack-backup.dump
```

Use `pg_restore --clean --if-exists` instead when restoring over a database that already has the
schema deployed (e.g. rehearsing a restore over a copy of production), so existing objects are
dropped before being recreated rather than causing "already exists" errors.

## Out of scope here

- **Physical backup / point-in-time recovery** (`pg_basebackup`, WAL archiving) — a release-gate
  concern once a real deployment target and RPO/RTO are defined, not exercised by this smoke test.
- **`pg_dumpall`** for a whole-cluster backup including role definitions — only relevant if a
  cluster's roles are not already provisioned by the roles-and-grants script.
- **SQLite** — SQLite is not a PostgreSQL backup or disaster-recovery format (plan §1); its own
  backup procedure is documented separately in
  `docs/operations/sqlite-limitations-and-configuration.md`.
