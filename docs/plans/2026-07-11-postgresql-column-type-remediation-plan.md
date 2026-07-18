# PostgreSQL Column-Type Remediation Plan

**Date:** 2026-07-11
**Status:** Proposed
**Scope:** Review whether JobTrack is making optimal use of PostgreSQL-native column types and
indexes after phases 1-3 plus the external HTTP API work. This plan covers only substantial
improvements; it deliberately excludes cosmetic type churn.

## 1. Current Assessment

The schema already uses several PostgreSQL features well:

- `timestamptz`, `date`, and `time` are used according to the Noda Time boundary decisions.
- `numeric(19, 6)` and `numeric(18, 2)` avoid binary floating-point on money/duration paths.
- `jsonb` is used for variable-shaped audit before/after payloads.
- `uuid` is used for audit correlation IDs.
- `tstzrange` generated columns and GiST indexes are used for `work_session` overlap discovery.
- `daterange`/`tstzrange` expressions plus GiST exclusion constraints enforce non-overlap for
  schedule versions, rates, overrides, and priced additive exceptions.
- seeded reference tables are used instead of PostgreSQL enums, which is appropriate for
  cross-provider equivalence and stable public numeric IDs.

The remaining opportunities are concentrated in three areas: range columns for all effective
periods, a native hierarchy path representation, and database-owned case-insensitive identity
uniqueness.

## 2. Test Review

The existing database tests are broadly worthwhile. The shared contract-test pattern is doing useful
work: it proves PostgreSQL and SQLite observable behavior together, and the concurrency tests cover
the invariants most likely to fail under real use. The PostgreSQL performance tests are also useful
where they assert plan shape at representative scale rather than timing tiny fixtures.

No existing test should be removed as part of this plan. The weak point is future test growth: the
type changes below could easily add low-value tests that only assert implementation trivia. Avoid
that. Prefer strengthening existing contract/performance tests over adding parallel test files that
check column names or extension installation in isolation.

### 2.1 Keep and Extend Existing Behavioral Contracts

Useful existing coverage to preserve:

- rate overlap, adjacency, different-user, different-node, and concurrent-conflict contract tests;
- work-session finite/open interval, boundary-touching, cross-leaf, cross-user, and concurrent
  overlap tests;
- hierarchy/readiness contract tests that compare PostgreSQL stored functions and SQLite recursive
  equivalents;
- Identity duplicate-normalized-name and concurrent insert tests; and
- PostgreSQL plan-shape tests where the fixture is large enough that the assertion is meaningful.

Improve coverage without adding broad new suites by extending these same tests:

- When generated range columns are added, have existing insert/overlap tests also read back the
  generated range for one finite and one unbounded row and assert it matches the scalar columns.
  This turns existing behavior tests into drift tests without duplicating every scenario.
- When range predicates are rewritten, update the existing rate-boundary and worker-overlap
  performance tests to assert the GiST range index is used on representative data. Do not add
  separate tests that merely assert a column exists.
- When username case-insensitive uniqueness is added, extend
  `AppUserAndIdentitySchemaContractTestsBase` with one bypass-normalizer case; do not add another
  Identity-store test unless normal `FindByNameAsync` behavior actually changes.
- If `ltree` is adopted, reuse the existing hierarchy/readiness and hierarchy performance tests as
  the acceptance surface. Add only the minimum direct path-maintenance tests needed to prove move
  correctness and concurrency, because the public contract remains ancestor/descendant/readiness
  behavior rather than the path string itself.

### 2.2 Tests to Avoid or Remove if Introduced

Do not add or keep tests whose only assertion is one of these:

- `CREATE EXTENSION` succeeded. Extension availability is only useful when a query or constraint
  uses it correctly.
- A generated column has a particular PostgreSQL catalog spelling. Assert behavior and query plan
  use instead.
- A PostgreSQL-only projection is exposed through EF when the application intentionally continues
  to map scalar portable columns.
- Native enum/domain/citext types exist without proving a behavioral improvement or compatibility
  constraint.
- Micro-benchmark timing on tiny fixtures. Tiny fixtures may legitimately use sequential scans; plan
  tests should run at accepted scale, or not run at all.

If any such tests are added during implementation, remove them in the same change unless they catch
a real regression not covered by behavior, provider-equivalence, drift, or plan-shape tests.

## 3. Recommended Remediations

### 3.1 Promote Effective Periods to Stored Range Columns

Several tables model intervals as `start`/`end` columns and repeatedly reconstruct a range in
constraints, functions, and predicates:

- `user_schedule_version`: `effective_start`/`effective_end` reconstructed as `daterange(...)`
- `user_cost_rate`: `effective_start`/`effective_end` reconstructed as `tstzrange(...)`
- `node_rate_override`: `effective_start`/`effective_end` reconstructed as `tstzrange(...)`
- `user_schedule_exception`: `started_at`/`finished_at` reconstructed as `tstzrange(...)`
- `personal_access_token`: validity is currently represented by `created_at`, `expires_at`, and
  `revoked_at`, with no queryable validity range

`work_session` already demonstrates the better pattern with a stored generated `session_range`.

**Work:**

- Add stored generated range columns:
  - `user_schedule_version.effective_range daterange`
  - `user_cost_rate.effective_range tstzrange`
  - `node_rate_override.effective_range tstzrange`
  - `user_schedule_exception.exception_range tstzrange`
  - optionally `personal_access_token.validity_range tstzrange` for active-token listing/expiry
    cleanup if query patterns justify it
- Rewrite exclusion constraints to use the generated range columns rather than expression-built
  ranges.
- Add GiST indexes for query paths that test intersection/containment, not just overlap
  constraints.
- Rewrite `resolve_rate`, `user_rate_boundaries`, and schedule/rate query predicates to use
  `@>` and `&&` against the generated columns where that improves the plan.
- Keep `start`/`end` scalar columns as the public EF/domain mapping unless a measured reason exists
  to expose range values to C#.

**Tests first:**

- Extend existing contract tests to prove generated columns match scalar start/end values for one
  finite and one unbounded row per affected table.
- Keep existing non-overlap/adjacency/concurrency tests as the main correctness proof; they should
  fail if rewritten constraints are missing.
- Update existing `EXPLAIN`/performance tests for rate boundary discovery and priced-exception
  lookup so they prove GiST indexes are used on representative data.
- Do not add catalog-only tests that merely assert generated columns exist.

### 3.2 Evaluate `ltree` for Job Hierarchy Paths

The job hierarchy currently uses adjacency-list `parent_id` plus recursive CTEs/stored functions
for ancestor, descendant, readiness, prerequisite, move validation, and nearest node-rate override
queries. This is correct, but it repeats recursive work across several hot query families.

PostgreSQL's `ltree` extension can store a materialized path and provide indexed ancestor/
descendant checks with GiST/GiN operators. This is a strong fit for:

- `job_node_ancestors`
- `job_node_descendants`
- prerequisite ancestor/descendant prohibition
- inherited prerequisite discovery
- nearest-ancestor node-rate override resolution
- subtree cost/report traversal bounds

**Work:**

- Run a focused spike against the existing performance scales before committing to schema changes.
- Add an `ltree` path column on `job_node` only if the spike demonstrates meaningful query-plan or
  latency improvement without weakening move concurrency.
- Maintain the path transactionally in the canonical `move_job_node` stored function, updating the
  moved node and every descendant under the existing deterministic advisory locks.
- Preserve `parent_id` as the authoritative portable hierarchy relationship; `ltree` is a
  PostgreSQL-optimized projection, not the cross-provider contract.
- Keep SQLite on recursive queries or a separate provider-specific projection only if needed.

**Tests first:**

- Reuse existing hierarchy/readiness contract tests as the public behavior proof.
- Add focused direct path-maintenance tests only for create/move/decompose cases that can drift from
  `parent_id`.
- Keep race tests proving concurrent moves and prerequisite writes still allow exactly one
  conflicting operation to succeed.
- Update existing performance tests to compare recursive CTE plans against `ltree` plans for deep,
  broad, and mixed trees.
- Do not test the path string format beyond what is needed to prove operator correctness and drift
  prevention.

**Exit rule:**

Do not adopt `ltree` unless it beats the current recursive functions on accepted performance scales
and does not materially complicate the move/prerequisite concurrency proof.

### 3.3 Consider Multirange Storage for Weekly Schedule Intervals

`user_schedule_interval` stores `day_of_week`, `start_time`, `end_time`, and `crosses_midnight`.
The schema intentionally leaves within-version interval normalization as a soft application concern.
If schedule editing or cost expansion becomes a correctness/performance hotspot, PostgreSQL
`int4range`/`int4multirange` can represent minute-of-week segments directly.

**Potential design:**

- Store or generate a `minute_of_week int4multirange` value for each interval, with a week encoded
  as `[0, 10080)`.
- Non-crossing intervals become one `int4range`; crossing-midnight intervals become two ranges.
- Add an exclusion constraint preventing overlapping `minute_of_week` ranges within one
  `schedule_version_id`, if the product decision changes from "should normalize" to "shall reject
  overlap".
- Use the multirange as a PostgreSQL query/performance projection only; keep Noda Time domain
  expansion authoritative for DST gap/fold resolution.

**Tests first:**

- Extend existing schedule-version/interval contract tests for Monday/Sunday, midnight, and
  crossing-midnight projection behavior.
- If overlap rejection is adopted, add adjacent-valid and overlap-invalid cases to the existing
  schedule interval contract suite.
- Use equivalence tests proving PostgreSQL minute-of-week projection does not change domain schedule
  expansion semantics.
- Do not add tests for multirange storage unless a product decision makes it a real invariant or a
  performance spike proves it is needed.

**Exit rule:**

Do not add this until a product decision closes whether overlapping weekly intervals are a hard
database invariant. The current soft requirement does not justify adding multirange complexity by
itself.

### 3.4 Add Database-Owned Case-Insensitive Username Uniqueness

`identity_user.normalized_user_name text UNIQUE` relies on application code consistently computing
and storing the normalized name. ASP.NET Core Identity expects a normalized-name field, so this is
not wrong, but PostgreSQL can enforce case-insensitive uniqueness more directly.

**Work:**

- Evaluate `citext` or a unique expression index on `lower(user_name)` as a defense-in-depth
  constraint alongside `normalized_user_name`.
- Prefer a unique expression index if `citext` complicates EF/SQLite equivalence or Identity store
  behavior.
- Keep `normalized_user_name` for ASP.NET Core Identity compatibility.

**Tests first:**

- Extend `AppUserAndIdentitySchemaContractTestsBase` with a direct database contract test inserting
  two rows with usernames that differ only by case while deliberately bypassing the application
  normalizer.
- Rely on existing Identity store tests unless implementation changes `FindByNameAsync` behavior.

## 4. Not Recommended

### 4.1 PostgreSQL Native Enum Types

Do not replace seeded lookup/reference tables with PostgreSQL enum types for `Achievement`,
`NodeKind`, `Priority`, roles, or schedule-exception effects.

Reasons:

- the public contracts already use stable numeric enum values;
- SQLite needs equivalent enforcement;
- FK-backed reference tables are easy to inspect, grant, and join;
- PostgreSQL enum migrations are more awkward for compatibility than inserting seeded reference
  rows; and
- no current query path benefits materially from enum column storage.

### 4.2 PostgreSQL Domain Types for Money and Rates

Do not introduce PostgreSQL `DOMAIN` types for `money`, `hourly_rate`, or non-negative decimals yet.

Reasons:

- the constraints are simple, table-local, and already explicit;
- EF/provider mapping would become more complex;
- SQLite still needs its own enforcement;
- domain types do not solve the main cost-engine risks, which are interval/range and allocation
  semantics rather than scalar decimal validation.

### 4.3 `inet`/`cidr` Columns

Do not add `inet`/`cidr` until the product stores client IP addresses, trusted networks, or audit
source addresses in the database. Current reverse-proxy trust configuration lives in host
configuration, not schema data.

## 5. Implementation Order

Use TDD and preserve PostgreSQL/SQLite public equivalence:

1. Extend existing tests first, favoring behavior/provider-equivalence/plan-shape assertions over
   new schema-shape tests.
2. Add generated effective-range columns and rewrite range constraints/functions.
3. Add performance evidence for range-column query plans.
4. Add database-owned username case-insensitive uniqueness.
5. Spike `ltree` on accepted hierarchy performance scales.
6. Adopt `ltree` only if the spike clears the exit rule.
7. Revisit weekly schedule multirange only after a product decision makes interval overlap a hard
   invariant.

## 6. Completion Criteria

This remediation is complete when:

- every effective-period table has a single canonical PostgreSQL range projection, or a documented
  reason it does not need one;
- PostgreSQL range predicates in stored functions and hot queries use indexed generated range
  columns where measurable;
- direct database tests prove username case-insensitive uniqueness cannot be bypassed by malformed
  normalized-name input;
- `ltree` is either adopted with correctness/race/performance evidence or explicitly rejected with
  spike results; and
- no added test only asserts implementation trivia without catching a realistic behavior, drift,
  compatibility, or plan regression; and
- no PostgreSQL-only type change weakens SQLite conformance or the public .NET contract.
