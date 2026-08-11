# ADR 0050: Job-description search is Unicode-aware and case-insensitive

**Status:** Accepted
**Closes:** scalability follow-up plan
(`docs/plans/2026-07-25-scalability-follow-up-plan.md`) §2.3 search-parity decision.

## Context

Awaiting Progress originally filtered descriptions in the functional core with
`StringComparison.OrdinalIgnoreCase`. Moving filtering and paging into each provider's query removed
the installation-wide materialization, but SQLite's built-in `lower()` only folds ASCII. PostgreSQL
therefore matched descriptions such as `Ångström` when searched as `ångström`, while SQLite did not.
The common `IJobQueries` contract must not expose provider-dependent search results.

## Decision

Job-description search remains arbitrary-substring, Unicode-aware, and case-insensitive. The shared
provider contract includes non-ASCII case pairs as well as ordinary ASCII text.

PostgreSQL continues to use its Unicode-aware `lower()` translation. SQLite uses a deterministic
per-connection scalar function implemented with `StringComparison.OrdinalIgnoreCase`; every
`SqliteJobTrackDbContext` created by the provider factory registers that function before issuing
queries. Filtering, ordering, and paging remain database-side on both providers.

No search index is added. The production-scale zero-match measurement recorded in
`docs/traceability/performance-budgets.md` remains below the threshold that would justify changing
the cross-provider search capability.

## Consequences

- PostgreSQL and SQLite pass the same shared Unicode search contract.
- SQLite no longer relies on its ASCII-only `lower()` for Awaiting Progress search.
- Any future indexed search design must preserve these observable semantics or supersede this ADR
  with an explicit product-visible change.
