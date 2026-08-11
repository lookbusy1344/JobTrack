# ADR 0011: Schema deployment-script format, versioning, and supported upgrade window

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 8

## Decision

Schema changes are delivered as **forward-only, source-controlled SQL scripts**, one file per schema version per provider, under `database/{postgresql,sqlite}/schema-versions/`. Naming: `NNNN_short-description.sql`, where `NNNN` is a zero-padded, strictly increasing integer version (matching the plan's existing `schema-versions/` directory layout, §3). There is no down-migration script — a bad version is fixed forward by a new version, never edited or rolled back in place, consistent with "forward-only" (plan §2, §6.1).

Each script:

- runs inside one transaction where the provider supports transactional DDL (PostgreSQL: yes, for the vast majority of statements; SQLite: yes for ordinary DDL, with documented exceptions for operations SQLite cannot run inside a transaction);
- is idempotency-guarded only in the sense that the deployment tool refuses to re-apply a version already recorded as applied (not via `CREATE IF NOT EXISTS` scattered through the script body); and
- is immutable once merged — a merged script's content and checksum never change; a mistake ships as a new subsequent version.

The deployment tool (§6.1) records, per applied version: schema-version identifier, a checksum of the script content (detects accidental or malicious post-merge edits), the application version that applied it, the acting identity, and a timestamp. It refuses to apply a script whose checksum does not match a previously recorded application of the same version number, and refuses to apply an unknown/newer version than the tool itself understands (fail closed rather than guessing forward compatibility).

**Supported upgrade window:** the deployment tool supports upgrading from **any** previously released schema version to the current version in one run (applying every intervening version's script in order) — there is no "upgrade from the last N versions only" restriction, because this is a from-empty-state-or-any-prior-version greenfield system without an established multi-year upgrade backlog yet. This window is revisited (and likely narrowed with an explicit deprecation policy) once the system has enough released versions that testing every historical upgrade path becomes disproportionate; that revision is itself a future ADR, not a silent policy drift.

## Consequences

- CI (§10 job 3/4) exercises deployment from empty state and from every previously recorded schema version, per provider, as part of the database gate (§6.7).
- The reference-data and scenario directories (`reference-data/`, `scenarios/`) are explicitly separate from `schema-versions/` (plan §3) — reference data ships as its own versioned scripts alongside schema, scenarios are development/test-only and never applied by the production deployment path.
- Production startup performs a schema **compatibility check only** (reads the recorded current version, compares to what the running application expects, fails closed on mismatch) — it never applies a schema-versions script itself (§6.1).
- See ADR 0012 (`0012-postgresql-lock-keys.md`) for the deployment lock the tool acquires before applying a version.
