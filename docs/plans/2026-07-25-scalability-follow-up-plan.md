# Scalability Follow-up Plan

**Date:** 2026-07-25
**Status:** Implemented (2026-07-25) — §2.1-§2.7 all closed (measured, and implemented where evidence
warranted); see §5 for the commit trail and per-finding disposition.
**Scope:** Remaining work identified by the 2026-07-24 scalability review after the initial
remediation commits. This plan supersedes neither that review nor accepted ADRs; it records the
unclosed performance and coherence work precisely enough to resume under TDD.

## 1. Completed baseline

The following follow-ups are already implemented and are not work items in this plan:

- Awaiting Progress assembles its multi-statement narrowed graph inside a repeatable-read
  transaction on both providers (`507d295f`).
- Readiness loads only the target ancestry, applicable prerequisite edges, and required-job
  subtrees (`10cac59c`).
- SQLite resolves out-of-scope prerequisite achievements in one recursive query rather than N+1
  queries (`9a899dec`).
- Hierarchy scale tests assert loaded-node bounds as well as elapsed time (`68b0ddd5`).
- Cost input assembly uses a scalar earliest-session aggregate and groups worker-owned in-memory
  data once (`6196d708`, `d506dda9`).
- The partitioner maintains active eligible-piece membership incrementally at boundaries
  (`f876cc89`).

## 2. Remaining findings

### 2.1 Request-scoped Awaiting Progress remains globally materialized

`GetAwaitingProgressAsync` currently receives all unfinished candidates from the port, then applies
ownership, subtree, description, offset, and limit inside `AwaitingProgressCalculator`. A staff
member viewing one home subtree consequently pays for every unfinished leaf in the installation.

**Target design:** introduce an internal `AwaitingProgressQueryFilter` carrying ownership, optional
subtree root, normalized search text, offset, and `limit + 1`. Pass it from `JobQueries` to
`IAwaitingProgressQueryPort`. Each provider must order candidates by the exact existing
priority/deadline/id ordering and page them before loading ancestry/prerequisite facts. The domain
calculator remains the authority for readiness and output mapping; it receives only the already
filtered page and therefore must not reapply those filters.

**Provider implementation:**

- PostgreSQL: use the existing canonical subtree function for an optional root and a LINQ candidate
  projection for owner/search/status filters; retain EF for ordinary candidate selection.
- SQLite: use one parameterized recursive CTE only when a root is supplied; never interpolate IDs.
- Both: retain a separate direct existence check for a supplied subtree root so an empty page does
  not conceal a nonexistent root.

**TDD:** add shared contract cases for owner, unassigned, search, subtree, ordering, offset/limit,
and a subtree with no candidates. Add a PostgreSQL scale test with a large unrelated unfinished
forest proving `NodesById` is bounded by page candidates plus ancestry/required facts.

### 2.2 Awaiting Progress candidate discovery still scans all `job_node` rows

Even after request scoping, the candidate predicate needs “childless, unarchived, unfinished”. At a
mature installation it is still a table scan.

**Target design:** first measure `EXPLAIN (ANALYZE, BUFFERS)` against the realistic-completion
fixture. If it remains material at the recorded budget, add provider-native support:

- PostgreSQL: a partial index supporting non-terminal `leaf_work` rows plus an anti-join branch for
  nodes without `leaf_work`; prove the actual plan uses it.
- SQLite: add only indexes SQLite can use for the equivalent joins; do not add a misleading partial
  index without `EXPLAIN QUERY PLAN` evidence.

Do not add a maintained “unfinished” column: it would duplicate derived hierarchy state.

### 2.3 Search is an unindexed arbitrary-substring query

`LOWER(description).Contains(...)` is `%term%` matching. A B-tree expression index cannot serve it.

**Target design:** decide and record search capability parity before implementation:

- PostgreSQL uses `pg_trgm` plus a GIN index on `lower(description)` and a plan assertion.
- SQLite must either use an FTS5 table maintained transactionally by triggers, or deliberately
  restrict both providers to prefix search and document the product-visible semantic change.

The preferred option is FTS5 because it preserves substring-like discovery only if the product
accepts tokenizer semantics; otherwise this requires an ADR. Do not ship PostgreSQL-only search
behaviour behind the common `IJobQueries` contract.

### 2.4 Cost authorization performs redundant reads

Single-node cost requests currently obtain actor roles, ancestor owners, and cost inputs through
separate port calls; the cost-input port resolves roles again. This adds fixed latency and does not
give one coherent authorization/read snapshot.

**Target design:** create a lightweight `CostAccessInputs` port operation containing actor state,
roles, and ancestor ownership from one snapshot. Authorize before expensive worker materialization.
Remove the duplicate role fetch from `GetCostInputsAsync`; retain the bulk path’s one-snapshot
authorization shape. The application remains the authorization-policy owner.

**TDD:** command-count tests for one-node cost reads, authorization-denial tests proving worker data
is not loaded, and provider-specific snapshot/race tests.

### 2.5 Cost schedule and override reads retain avoidable history

Cost assembly loads all schedule versions for contributing workers and all time-overlapping node
overrides for those workers. Only schedule versions intersecting the cost window, and overrides on
loaded session ancestry, can affect the result.

**Target design:**

- Resolve each worker’s local-date window using the version’s own IANA zone, then query only
  overlapping schedule versions/intervals.
- Load worker sessions and extend ancestry before loading overrides; filter overrides by the final
  loaded node set and cost interval.
- Preserve the full worker-wide overlapping-session scope required by ADR 0017.

**TDD:** add long-history fixtures with obsolete schedule versions and unrelated overrides; assert
unchanged totals and bounded materialized-row counts on both providers.

### 2.6 Cost partitioning still has output-proportional worst cases

The active-set sweep removed redundant membership scans, but a deeply overlapping worker still
produces one allocation per active session per boundary. That output is intrinsically large for the
current trace model.

**Target design:** measure allocation/trace cardinality independently. If realistic heavy-worker
fixtures exceed the response limit, introduce a bounded aggregate representation only through an
explicit API/domain design decision; do not silently truncate or round allocations.

### 2.7 Measurements need a coherent benchmark protocol

The hierarchy tests should explicitly warm a pooled data source before timing, dispose it after each
test, and report both cold and warm figures if cold-start behaviour is a supported budget. Existing
documentation must state which mode each recorded figure uses.

## 3. Delivery order and commits

1. Add shared contract tests for request-scoped Awaiting Progress; implement both ports; commit.
2. Add PostgreSQL/SQLite candidate-plan tests; add only evidence-backed indexes; commit.
3. Write an ADR deciding cross-provider substring-search semantics; implement the selected design
   with provider plan tests; commit.
4. Add cost authorization command-count/denial tests; consolidate access reads; commit.
5. Add long-history schedule/override tests; narrow those materializations; commit.
6. Improve benchmark warm-up/disposal protocol and update performance budgets; commit.
7. Re-run build, format, fast suite, targeted provider/web tests, then the full solution suite once;
   update this plan and the 2026-07-24 plan only with measured evidence.

## 4. Completion criteria

1. Awaiting Progress reads are proportional to the requested page/filter scope, except for facts
   mathematically required to establish readiness.
2. Candidate discovery and search have provider-plan evidence, not assumed indexes.
3. Cost authorization has no duplicate actor-role lookup and denies before worker materialization.
4. Long historical schedule/override data outside the cost window does not scale a cost request.
5. Performance documents distinguish cold from warmed measurements and every new bound has a test.
6. All changes pass the project commit gate; commits remain small and independently reversible.

## 5. Resolution (2026-07-25)

All seven findings closed in scoped commits, targeted provider/web tests plus the full
`fast-test.sh` suite passing after each, and one full `dotnet test JobTrack.slnx` run at the close
(surfaced one test-environment-contention flake, not a regression — see below). Findings §2.6 and
§2.7 deliberately share `63ba2ce5` because the cardinality measurement and warmed benchmark protocol
were one performance-test change.

- **§2.1 (`6cea5672`):** `AwaitingProgressQueryFilter` (ownership, subtree root, search text,
  offset/limit) now drives the port's own query — filtering, the exact descending-priority/
  ascending-deadline-nulls-last/ascending-id ordering, and paging all happen in SQL before
  ancestor/prerequisite facts load, on both providers. `AwaitingProgressCalculator` is readiness/
  output-mapping only. New PostgreSQL/SQLite contract-test coverage for owner, unassigned, search,
  subtree, subtree-with-no-candidates, and offset/limit paging.
- **§2.2 (`42427aac`):** measured first, per the plan's own directive. `EXPLAIN (ANALYZE, BUFFERS)`
  for the production-realistic default-page shape: ~34 ms at realistic-completion-ratio scale,
  dominated by the childless-check anti-join against the existing `job_node_parent_id_idx`, not a
  sequential scan. No partial index is evidence-backed; added a permanent regression benchmark
  instead of a schema change.
- **§2.3 (`40683c17`):** measured the worst case (a zero-match whole-tree search, which forces a full
  scan) — ~20 ms at combined-production-tree scale. Not material, so neither `pg_trgm`/GIN nor an
  ADR on cross-provider tokenizer/prefix semantics is currently warranted. Added a regression
  benchmark recording the finding (revised 700 ms after the full-suite contention noted below).
- **§2.4 (`de2546bb`):** added `ICostQueryPort.GetCostAccessInputsAsync`, returning actor roles and
  ancestor-chain owners from one snapshot; `CostQueries` authorizes from that single call before
  `GetCostInputsAsync`'s worker/session materialization, which no longer takes an actor id or
  re-resolves roles. Command-count contract tests prove the single-node read stays bounded and that
  a denied actor's read never opens the worker-materialization connection.
- **§2.5 (`138a1d4c`):** schedule versions are now prefiltered by a one-day-widened UTC window
  around the cost bounds (safe regardless of a version's own IANA zone); node-rate overrides load
  after `ExtendAncestryAsync` determines the final node set, filtered by that set (`RateResolver`
  can never consult an override elsewhere). `UserCostRate` stays worker-wide/time-only, correctly
  unfiltered by node. Contract tests prove a decade of superseded schedule versions and unrelated
  node overrides on decoy nodes change neither the total nor `WorkerCostInputs.NodeOverrides`.
- **§2.6 (`63ba2ce5`):** measured cost-trace cardinality for the heaviest realistic fixture (one
  worker, 5,000 sessions): 30,000 segments, 60% of `CostQueries.MaxCostTraceSegments`'s hard cap
  (50,000) — worth watching, not exceeding, so no bounded aggregate representation is introduced.
  Added a regression ceiling with headroom.
- **§2.7 (`63ba2ce5`):** `FullTableHierarchyLoadPerformanceTests` now explicitly warms its pooled
  `NpgsqlDataSource` before timing in every test (this project's `xunit.runner.json` sets
  `stopOnFail`, so a test can legitimately run alone and must not assume an earlier test already
  paid the JIT/connection cost). Isolating an unwarmed test previously showed ~550-570 ms cold vs.
  ~34-120 ms warm for the identical query. `docs/traceability/performance-budgets.md` records the
  full measurement trail and states the warm convention explicitly.
- **Full-suite contention (`2a22a590`):** the one full-solution-suite run required by step 7 above
  surfaced the §2.3 search ceiling flaking under the same shared-PostgreSQL-instance contention
  already documented for the broad-branch child-listing row (~450 ms contended vs. ~20 ms isolated)
  — revised to 700 ms with headroom, following that exact precedent, not a real regression.

### Post-implementation audit (2026-07-25)

A fresh audit found five gaps behind the original completion wording and closed them before treating
this plan as fully implemented:

- `GetCostAccessInputsAsync` grouped the actor-role and ancestor-owner reads in one method but did not
  start a transaction, so PostgreSQL read-committed and SQLite autocommit could still return facts
  from different snapshots. Both providers now pin those statements to one repeatable-read
  transaction; a shared contract test observes the transaction boundary.
- `GetReadinessInputsForNodesAsync` batched requested ancestor discovery but still loaded every
  distinct required-job subtree with a separate command. Both providers now load the union of all
  required subtrees in one recursive query, keeping command count constant as prerequisite count
  grows, and pin the entire multi-statement assembly to one repeatable-read transaction.
- SQLite cost assembly indexed schedules, exceptions, rates, and overrides by worker but still
  rescanned the complete overlapping-session collection once per worker. Sessions are now indexed
  once with the same lookup shape, removing the remaining `O(workers × sessions)` in-memory pass.
- `FullTableHierarchyLoadPerformanceTests` warmed four locally-created pooled data sources but did
  not dispose them, contrary to §2.7's protocol. Each is now asynchronously disposed, the raw SQL
  fixture whitespace is clean, and the performance-budget text consistently records the revised
  700 ms contended search ceiling.
- A supplied Awaiting Progress subtree root was first expanded and fully materialized as an ID array
  before candidate selection. Both providers now compose their recursive subtree relation directly
  into the paged candidate query; a shared command-bound contract test proves subtree filtering adds
  no ID-materialization round trip. A production-scale regression additionally caught the true-root
  case spending ~762 ms traversing all 193,570 nodes despite being semantically equivalent to no
  subtree filter (~84 ms); the candidate query now short-circuits a permanent-root scope in SQL and
  stays within the existing warmed ceiling (originally 300 ms, later widened to 500 ms to absorb
  full-suite Postgres contention -- see performance-budgets.md).

The post-audit gate passed the warnings-as-errors build, format/verify, the 1,297-test fast suite,
70 affected dual-provider contract tests, and the production-scale root-scope benchmark. The
required full-solution run passed every correctness suite; its unchanged rate-resolution performance
row measured 50 ms against the 20 ms isolated ceiling under all-project PostgreSQL contention, then
passed immediately when rerun alone. No ceiling was weakened for that environmental result.

No ADR was needed in the original implementation: §2.2/§2.3/§2.6 resolved at the measurement step
without a design change, and §2.1/§2.4/§2.5/§2.7 were treated as internal-port/test-protocol changes.
The compatibility review below subsequently found that moving §2.1's search predicate had in fact
created a provider-visible semantic shift; ADR 0050 now closes that decision explicitly.

### Compatibility and hard-bound review (2026-07-25)

A further review of the completed range found and closed three issues:

- **Public API compatibility (`70626f59`):** the §2.1 move had replaced the public
  `AwaitingProgressCalculator.GetAwaitingProgress` overload even though the Domain surface is a
  post-library-gate compatibility commitment. The original ownership/subtree/search overload is
  restored alongside the narrowed candidate-mapping overload, with a regression test covering its
  full filtering contract.
- **Unicode search parity (`2a9c8338`, ADR 0050):** SQLite's built-in `lower()` is ASCII-only, so
  moving search from .NET `OrdinalIgnoreCase` into SQL made non-ASCII results provider-dependent.
  SQLite now uses a deterministic per-connection ordinal-ignore-case function; the shared provider
  contract proves `Ångström`/`ångström` parity on PostgreSQL and SQLite while filtering and paging
  remain database-side.
- **Effective trace bound (`c919adbd`):** `MaxCostTraceSegments` previously rejected only after the
  complete trace had been allocated. `CostSegmentPartitioner.PartitionBounded` now counts all active
  sessions in `N`, emits only requested-subtree allocations, and throws before crossing the remaining
  cross-worker trace budget. Hierarchy totals no longer construct a trace they discard.

Each remediation passed the warnings-as-errors build, format verification, its affected targeted
tests, and the fast suite before its separate commit.
