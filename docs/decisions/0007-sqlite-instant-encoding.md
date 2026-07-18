# ADR 0007: Canonical SQLite instant encoding and precision

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 4

## Decision

SQLite stores every instant (`Instant`-typed value at the domain boundary — see ADR 0016 (`0016-noda-time.md`)) as a **signed 64-bit integer UTC tick count**, using .NET's `NodaTime.Instant` tick precision (100-nanosecond ticks since the Unix epoch, matching `Instant.ToUnixTimeTicks()`), in an `INTEGER` column. This is the one documented integer UTC epoch encoding required by plan §6.4, applied uniformly to every temporal column: session start/end, schedule effective-dates where they carry a time component, audit timestamps, and the schema-deployment record's applied-at timestamp.

Rejected alternatives:

- **ISO-8601 text** (SQLite's other common temporal convention) — sorts correctly only for a fixed-width format, is more storage, and requires text parsing on every comparison; the GiST-equivalent overlap triggers (§6.4) need cheap integer range comparisons.
- **Julian day real** (SQLite's native `julianday()`) — a `REAL`/`double` representation, which is prohibited on the duration/money-adjacent temporal path by the no-`double` rule (ADR 0009 (`0009-decimal-precision-and-allocation.md`) extends this reasoning to instants: floating point cannot exactly represent arbitrary tick counts).
- **Unix milliseconds/seconds** — insufficient precision to match PostgreSQL `timestamptz`'s microsecond precision without a documented, separate rounding rule; ticks avoid a precision mismatch between providers.

PostgreSQL's `timestamptz` (microsecond precision) is the provider of record; the SQLite tick encoding is defined so that round-tripping through the domain's `Instant` representation never loses precision on either provider, and the persistence conformance suite asserts this equivalence directly (an instant written on one provider and read on the other, via the domain layer, compares equal).

## Consequences

- The shared EF model configuration (§7.4) supplies one value converter (`Instant <-> long` ticks) for `JobTrack.Persistence.Sqlite`, distinct from `JobTrack.Persistence.PostgreSql`'s `Instant <-> timestamptz` mapping (via Npgsql's NodaTime plugin) — both are documented as the one "provider-divergent mapping" case permitted by §7.4.
- Schema-introspection tests (§6.6) assert the column type/affinity for every temporal column on SQLite is `INTEGER`, not `TEXT` or `REAL`.
- Sub-tick precision is never required or exposed; if a future requirement needs finer resolution, this ADR is revisited rather than silently narrowing tick meaning.
