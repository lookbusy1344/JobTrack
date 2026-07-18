# SQLite limitations and configuration

**Closes:** Implementation plan §6.7 gate item "SQLite limitations and configuration requirements
are documented", and the corresponding requirement in §6.4. SQLite is a complete, supported backend
for embedded/single-node deployments (plan §1) — this document is about its operational envelope and
required per-connection setup, not a reduced feature list. Where SQLite's *domain* behaviour must
match PostgreSQL's, that equivalence is asserted by the shared contract suite (§6.6); this document
only covers what differs operationally.

## Required per-connection configuration

Every application connection must issue, immediately after opening:

```sql
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
```

Unlike PostgreSQL, SQLite enforces neither foreign keys nor a lock-wait timeout by default per
connection — both are opt-in `PRAGMA`s scoped to the connection that sets them, not database-wide
settings. Every SQLite contract test in `tests/JobTrack.Database.ContractTests` issues both pragmas
after opening its connection (`SqliteJobNodeSchemaTests`, `SqliteWorkSessionSchemaTests`, etc.) for
this reason. The 5-second busy timeout is the value proven against this project's race tests (§6.6);
it is not a magic number restated per call site (`RateSchemaContractTestsBase` and siblings document
it as "SQLite needs `PRAGMA foreign_keys`/`busy_timeout` set per connection; PostgreSQL needs
nothing").

`JobTrack.Persistence.Sqlite`'s 7 write-side command ports share one constant,
`SqliteConnectionPragmas.ConfigureConnectionSql`, rather than duplicating the pragma string per
call site. `JobTrack.Identity` opens its own `SqliteConnection` independently (ADR 0022's
no-ASP.NET-Core-dependency boundary keeps it out of `JobTrack.Persistence.Sqlite`) — it sets
`foreign_keys`/`busy_timeout` via `SqliteConnectionStringBuilder`'s connection-string keywords, and
`journal_mode`/`synchronous` via `SqliteWalPragmaInterceptor`, an EF Core `DbConnectionInterceptor`
registered in `AddJobTrackIdentitySqlite`. The schema-deployment tool (`JobTrack.Database`) does not
need `foreign_keys`/`busy_timeout` — deploying DDL and recording `schema_version` rows takes no
foreign-key-checked writes and uses `SqliteDeploymentLockStrategy`'s `BEGIN IMMEDIATE` (below)
rather than a busy-wait retry — but its `DeployAsync` does issue `PRAGMA journal_mode = WAL;` once
after opening its connection, since `journal_mode` is a database-file property (sticky once set,
unlike the other three pragmas) and this fixes it for the file from first deployment rather than
relying on the first application connection to set it later.

**`journal_mode = WAL`** lets readers proceed without blocking on the single writer's in-progress
transaction, which matters once a deployment has more than one concurrent reader.
**`synchronous = NORMAL`** is safe under WAL (unlike under the default rollback journal, where
`NORMAL` risks a corrupt database on power loss) and avoids `FULL`'s fsync cost on every commit.
Both are fixed values, not configurable per deployment.

## Single-writer operational envelope

SQLite serializes all writers through one file-level write lock; there is no MVCC writer
concurrency the way PostgreSQL provides it. `SqliteDeploymentLockStrategy` relies on this directly:
schema-deployment transactions use `BEGIN IMMEDIATE` (serializable, non-deferred) to take the write
lock immediately, which serializes concurrent deployment-tool runs without needing an
advisory-lock-equivalent primitive (§6.4) — SQLite has none. The same envelope applies to
application writes: a second writer blocks (up to `busy_timeout`) rather than proceeding
concurrently. Load/concurrency *performance* expectations differ from PostgreSQL as a result (§6.5
does not hold SQLite to the same query-plan/latency budgets), but a successful operation must still
have the same domain effect and the same stable public error category as PostgreSQL (§6.4).

## Differences from PostgreSQL with no SQLite equivalent

- **No roles or `GRANT`.** The five PostgreSQL roles from §6.1
  (`jobtrack_owner`/`jobtrack_schema_deployer`/`jobtrack_application`/`jobtrack_readonly`/
  `jobtrack_emergency_reset`, `database/postgresql/roles/jobtrack-roles-and-grants.sql`) have no
  SQLite analogue — SQLite has no server process or login concept to grant against. A SQLite
  deployment's privilege separation is therefore an OS-level file-permission concern (who can open
  the `.db` file for writing at all), not a database-level one; there is no equivalent of the
  role-grants contract test (`TC-DB-ROLES-001`) for SQLite, and none is planned.
- **No exclusion constraints.** PostgreSQL's `EXCLUDE`/GiST constraints (overlap and graph guards,
  §6.3) are replaced by triggers plus recursive CTEs on SQLite (§6.4); these are asserted to produce
  the same domain effect and error identity, not the same mechanism.
- **No physical replication / point-in-time recovery.** SQLite has nothing analogous to WAL
  archiving or a standby server; see the backup procedure below for what SQLite offers instead.

## Temporal encoding

Every temporal column is a signed 64-bit UTC tick count (`Instant.ToUnixTimeTicks()` precision) in
an `INTEGER` column — see ADR 0007 (`docs/decisions/0007-sqlite-instant-encoding.md`) for the full
rationale and the rejected alternatives (ISO-8601 text, Julian-day `REAL`). Schema-introspection
tests assert every temporal column's affinity is `INTEGER`, never `TEXT` or `REAL`.

## Backup procedure

SQLite has no server process to connect a backup tool to; back up the database file itself, using
one of:

- **Online-safe copy via the `sqlite3` CLI's `.backup` command** (preferred for a running
  application): produces a consistent snapshot even while a writer holds the database, without
  requiring application downtime.

  ```sh
  sqlite3 jobtrack.db ".backup jobtrack-backup.db"
  ```

- **`VACUUM INTO`** (equivalent consistency guarantee, usable from any connection, also compacts
  the copy):

  ```sql
  VACUUM INTO 'jobtrack-backup.db';
  ```

- **Plain file copy** (`cp`), only when the application is stopped and no connection holds the
  database open. A bare `cp` of a live, WAL-mode database's main file without its `-wal`/`-shm`
  sidecar files is unsafe and can produce a corrupt or inconsistent copy — use one of the two
  options above for any backup taken while the application might be running.

Restoring is simply replacing the deployment's database file with the backup file before the
application next opens it; there is no separate restore tool. See
`docs/operations/postgresql-backup-restore.md` for the PostgreSQL-specific `pg_dump`/`pg_restore`
smoke test and procedure — SQLite is not a PostgreSQL backup or disaster-recovery format (plan §1),
so the two documents describe genuinely separate procedures, not a shared one.
