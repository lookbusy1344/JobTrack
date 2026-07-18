# ADR 0015: Provider-specific bootstrap state machine and permanent-root guard

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 13

## Decision

The atomic bootstrap (command defined by ADR 0005 (`0005-bootstrap-sequencing.md`)) must guarantee, under concurrent invocation, that **at most one** first administrator, permanent root, and initialised marker are ever created — a partial unique index alone gives only "at most one row," not "exactly the intended atomic sequence," so the following mechanism composes index, lock, and transaction:

1. **Initialised-marker table** with a single-row constraint: a `CHECK` (PostgreSQL) or trigger-enforced (SQLite) constraint that the table can hold at most one row, plus the row itself only ever being insertable once. This is the authoritative "has bootstrap happened" flag, checked first.
2. **Transaction-scoped advisory lock** on the fixed bootstrap key (see ADR 0012 (`0012-postgresql-lock-keys.md`)) acquired at the start of the bootstrap transaction, before any read of the initialised-marker table — this serializes concurrent bootstrap attempts on PostgreSQL so the check-then-act on the marker table is race-free even though the marker table's own constraint is (by itself) only a last-resort backstop. On SQLite, the immediate write transaction (§6.4) provides the equivalent serialization, since SQLite has no separate advisory-lock primitive and single-writer semantics already serialize the transaction.
3. Inside the locked transaction: re-check the initialised-marker table (defensive re-check even after acquiring the lock, since the lock only excludes other bootstrap attempts — it does not exclude a prior, already-committed bootstrap); if already initialised, throw the stable `JobTrackException` for "already bootstrapped" rather than silently succeeding or silently overwriting.
4. If not yet initialised, in the same transaction: insert the `app_user` row, the Identity credential row, the permanent root `job_node` (with its undeletable/un-re-parentable guard armed — see below), and the initialised-marker row, then commit.

**Permanent-root guard arming point.** The root's undeletable/un-re-parentable guard (a `CHECK`/trigger condition referencing the initialised-marker row, since minimum-cardinality-one cannot be enforced by a row trigger on an empty table, plan §6.2 item 4) is armed **at the same commit** that inserts the initialised marker — not in a separate subsequent statement. This closes the window where a root could theoretically exist without its guard active: the guard-arming condition and the root's existence become true atomically, in one transaction, so there is no observable intermediate state where the root exists but is deletable.

## Consequences

- A concurrent-bootstrap race test (plan §6.6) issues two simultaneous bootstrap attempts and asserts exactly one succeeds and the other observes the stable "already bootstrapped" exception — never two roots, never a partially-written admin/root pair.
- The guard's `CHECK`/trigger condition is written once, referenced by both the root-delete and root-reparent code paths, and unit-tested for both PostgreSQL and SQLite (§6.6 schema-introspection and race-test categories both apply).
- This ADR is the provider-specific mechanics; ADR 0005 (`0005-bootstrap-sequencing.md`) is the library-level command shape and dependency boundary. Neither is complete without the other.
