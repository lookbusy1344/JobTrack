# Development scenarios

This directory is the layout slot reserved by the implementation plan (§3) for **provider-neutral
development scenarios** — reproducible, non-production sample data kept deliberately separate from
schema versions (`../postgresql/schema-versions`, `../sqlite/schema-versions`) and from reference
data, per the schema-deployment substrate rules (§6.1: "separates schema/reference data from
development scenarios").

## Where the golden and generated scenarios actually live

Through the database and library phases (Phases 1–2) the golden and generated scenarios required by
plan §5.2 are authored **as code**, not as SQL/data files here, because they must run identically
against both providers and feed the pure domain engine:

- **Deterministic golden datasets and seeds** — built by the shared fixtures in
  `tests/JobTrack.TestSupport` (`PostgreSqlDatabaseFixture`, `SqliteDatabaseFixture`) and the
  per-slice `*SchemaContractTestsBase` classes in `tests/JobTrack.Database.ContractTests`, so one
  scenario definition drives both PostgreSQL and SQLite from the same source.
- **Generated reproducible data with recorded seeds** (§6.6) — property tests in
  `tests/JobTrack.Domain.Tests` (e.g. the cost-partition oracle and hierarchy-reconciliation
  property suites); failing seeds are preserved as regression fixtures in the same test project.
- **The canonical cost golden results** (§5.2 final bullet, §7.2) — encoded as expected values in
  the `JobTrack.Domain.Tests/Costing` suite, cross-checked against the independent per-tick oracle.

Keeping these in code lets the same scenario assert exact rational allocation in the domain engine
*and* provider-equivalence at the database boundary, which raw SQL seed files could not.

## What belongs here instead

Add files to this directory only for scenario data that is genuinely provider-neutral **standalone
SQL or data** — e.g. a large hand-authored demonstration hierarchy loaded by the AdminCli for manual
exploration, or a shared load-test dataset consumed by both providers' verification scripts. Such a
file must never be applied by production startup and must carry its own recorded seed/version so it
stays reproducible. As of Phases 1–2 there are no such files; this README keeps the reserved slot
tracked and self-documenting rather than an empty, unexplained directory.
