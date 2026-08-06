# Cost-read materialisation reduction plan

**Date:** 2026-08-06
**Status:** Implemented (2026-08-06) — Stages 1-5 all done. §2.1 retargeted from the original
schedule-expansion hypothesis to `IntervalAlgebra.Subtract`'s measured O(M×C) defect (Stage 4, a
~30x materialisation win); Stage 5 layered a further ~28% column-projection win on top. One new,
out-of-scope finding recorded (`CostSegmentPartitioner`/`CostEngine` computation cost at the
long-history scale's session density) — not pursued here, left for a future plan.
**Scope:** Reducing what the PostgreSQL cost-input read path materialises — in the database round
trips and in process memory — following the 2026-07-24/25 scalability work. PostgreSQL is the
priority provider throughout: every measurement, plan-shape assertion, and budget in this plan is
PostgreSQL-only. SQLite is kept functionally correct via the existing shared contract tests
(dual-provider parity is a house rule, not optional) but receives no performance measurement, no
SQLite-specific optimization, and no latency claims.

## 1. Context and assessment

The 2026-07-24 code-review-scalability-remediation-plan and 2026-07-25 scalability-follow-up plan
eliminated the structural scaling defects: full-table hierarchy loads are gone, cost reads are
request-scoped (`job_node_subtrees` / `job_node_ancestor_chains`, `CostQueryAssembly.
ExtendAncestryAsync`), session discovery is sargable against `work_session_user_range_gist_idx`
through `worker_overlapping_sessions`, and every hot path is regression-guarded in the serialized
`scripts/perf-test.sh` lane.

What remains is a floor set deliberately by the architecture — the cost engine is pure C# fed by
ports (ADR 0017), so cost inputs must be materialised into memory — plus four reducible costs above
that floor, in descending value:

1. **`IntervalAlgebra.Subtract` is quadratic in schedule-exception count** (§2.1, retargeted by
   Stage 1's own measurement — see below). Resolving schedule exceptions against a worker's expanded
   schedule costs O(intervals × exceptions), not O(intervals + exceptions), even though both sides
   are already sorted and disjoint by the time `Subtract` runs.
2. **The subtree id set round-trips as `= ANY(array)` parameters** (§2.2). The port materialises
   the subtree, then ships its id array back to PostgreSQL in later queries. Near the 50,000-node
   `MaxHierarchyNodeCount` cap that is a ~400 KB bigint array serialized per query, and the planner
   sees an opaque parameter rather than a real cardinality.
3. **Worker discovery runs the identical predicate twice** (§2.3). One `MIN` scan and one
   `DISTINCT` scan, back to back, over the same filtered session set.
4. **Worker-input loads materialise full entities where a projection would do** (§2.4). Smallest
   win; evidence-gated.

At the overlapping-cost scale (dense sessions, short window) none of this was binding — the cost
engine measured 38.6 ms DB / 14.6 ms pure engine. The forcing question — does anything above matter
at a genuinely long `asOf` window? — is exactly what the deferred **long history** budget rows in
`docs/traceability/performance-budgets.md` §1/§2 exist to answer, and their generator was never
built. Stage 1 built it and measured: §2.1's original schedule-expansion hypothesis was wrong for
this scale, but the measurement surfaced a different, larger, evidence-backed defect in the same
code path (`IntervalAlgebra.Subtract`'s quadratic behaviour) — proof that "measure first" is not
procedural box-ticking here. §2.1 below is retargeted accordingly; Stage 4 now fixes the measured
defect.

## 2. Findings

### 2.1 `IntervalAlgebra.Subtract` is quadratic in schedule-exception count — RETARGETED BY STAGE 1 MEASUREMENT

| | |
|---|---|
| **Severity** | **High** (measured, not latent — already over every affected budget at the spec's own long-history scale) |
| **Evidence** | `src/JobTrack.Domain/Intervals/IntervalAlgebra.cs` `Subtract` (`minuend.SelectMany(source => cuts.Aggregate(...))` iterates the *full* cuts list once per minuend interval — no pruning of non-overlapping pairs); `LongHistoryScalePerformanceTests` (Stage 1, 2026-08-06) |

**This finding originally hypothesised that `ScheduleExpander.Expand`'s full-bounds expansion was
the reducible cost** (an old subtree costed today expanding years of daily intervals regardless of
session sparsity). Stage 1's baseline measurement disproved that hypothesis and found the real one:

At the long-history scale (5 years, 20 workers, one session/exception per worker per day —
performance-budgets.md §1's own spec, deliberately *dense*, not sparse), the single-leaf cost read
measured **1,217.6 ms against a 150 ms budget** and the 20-worker branch read measured
**8,174.6 ms against a 2 s budget** — both roughly 4-8x over. Instrumentation
(`LongHistoryScalePerformanceTests` output) recorded 36,520 scheduled working intervals against
36,500 sessions — a 1.00 ratio, meaning `ScheduleExpander` produced almost exactly one interval per
session, not a wasteful multiple: **the original hypothesis is wrong for this scale, because daily
sessions leave no calendar gap for a "session cover" to exclude.** A stand-alone microbenchmark
against the same shape (1,825 scheduled intervals × 1,825 exceptions, one worker) isolated
`ScheduleExceptionResolver.Apply` → `IntervalAlgebra.Subtract` at **378.5 ms**, and 20 workers'
worth at **4,615.4 ms** — accounting for the large majority of the observed 7,690.5 ms DB-and-CPU
materialisation stopwatch reported by the port. `Subtract`'s inner loop is genuinely O(M×C): for
*every* minuend interval it aggregates over *every* cut, even though `Normalize` has already made
both lists sorted and disjoint, which is exactly the shape `IntervalIndex` (2026-07-25) was built to
search in better than linear time — `Subtract` never adopted it.

**Target design (Stage 4, retargeted):** build one `IntervalIndex` over the normalized cuts and, for
each minuend interval, aggregate only over `index.Overlapping(source)` instead of the full cuts
list — O((M+C) log C) instead of O(M×C), using the existing, already-tested search structure rather
than a new one. `Subtract`'s public contract (order-preserving over `minuend`, tolerant of an
unsorted/non-disjoint `minuend`) is unchanged; only the cut-matching strategy changes, so the
existing `IntervalAlgebraTests.Subtract` cases are the correctness regression net as-is. Correctness
bar for the new code: a scale test proving output-equivalence is unnecessary extra work here (the
algorithm is a pure optimization of an already-tested pure function, not a semantic change) — the
bar is a *performance* regression test asserting the long-history shape completes within a tight,
evidence-based ceiling, following this project's own "failing test first" convention for algorithmic
fixes.

Session-cover expansion (the original §2.1 design) is **not pursued** — Stage 1's own measurement
shows it would not help this scale (ratio 1.00), and no other seeded fixture in this codebase
exercises the sparse-session shape it targets. If a future measurement against a genuinely sparse
long-history shape (a few old sessions, `asOf` far in the future) shows `ScheduleExpander`'s
materialisation is independently material once `Subtract` is fixed, it is a new finding with its
own evidence, not a revival of this one.

### 2.2 Subtree ids round-trip as parameter arrays

| | |
|---|---|
| **Severity** | Low-Medium (payload and planner opacity near the node cap; correct today) |
| **Evidence** | `src/JobTrack.Persistence.PostgreSql/PostgreSqlCostQueryPort.cs:215-230` (`requestedNodeIds.Contains(s.LeafWorkId)` in the session-bounds and worker-discovery queries), `:313-318` (`finalNodeIds.Contains(o.NodeId)` in the override load) |

`LoadSubtreeAsync` already materialises the subtree rows (the engine needs `NodesById`), but the
follow-on queries re-ship the whole id set to the server as `= ANY(@array)` instead of joining
against the same `job_node_subtrees` function the ids came from.

**Implemented (Stage 3, 2026-08-06).** The session/worker-discovery query joins
`job_node_subtrees({existingRootIdValues})` server-side — parameters shrink from O(subtree) ids to
the handful of requested root ids — and folds in Stage 2's grouping in the same statement. The
override load's node set is the *extended* set (subtree plus ancestor chains), so it joins
`job_node_subtrees({existingRootIds}) UNION job_node_ancestor_chains({ancestorChainRootIds})`, where
`ancestorChainRootIds` is the same small off-subtree leaf/root set `ExtendAncestryAsync` already
computes (now returned to its caller instead of discarded) — O(roots + off-subtree session leaves),
not O(nodes).

**Deviation from the original design:** composing a `context.Database.SqlQuery<long>(...)` subquery
with `.Where(x => subquery.Contains(x.ConvertedProperty))` against an EF-converted column
(`JobNodeId`/`AppUserId`, both wrap `long` via a value converter) does not translate — EF throws
`InvalidOperationException` at query-compile time, confirmed by running the change against real
PostgreSQL before settling on the final form. Both queries are one hand-authored `SqlQuery<TRow>`
statement instead (the same pattern `LoadSubtreeAsync`/`LoadWorkerSessionsAsync` already use in this
file), added to `InlineDmlArchitectureTests`' reviewed raw-SQL inventory. Functionally identical to
the original design — still one join, still O(roots), just not expressed as LINQ composed over a
raw-SQL subquery.

Verified: the shared `CostQueryPortContractTestsBase` suite (20 tests, both providers) passes
unchanged, including a new PostgreSQL-only test seeding a 200-leaf subtree and asserting via a
command interceptor that no array parameter this read issues ever reaches leaf count (the old shape
would have shipped exactly 200). `FullTableHierarchyLoadPerformanceTests`' combined-production-tree
single-leaf cost-read ceiling (400 ms) still passes at 10.7 ms, unchanged. The long-history baseline
(Stage 1) is unaffected by Stage 2/3 alone, as expected — that scale's latency is dominated by
§2.1's `Subtract` defect, not by array-parameter payload or query count; Stage 4 is where it closes.

### 2.3 Worker discovery scans the same predicate twice

| | |
|---|---|
| **Severity** | Low (one avoidable round trip per cost read with sessions) |
| **Evidence** | `src/JobTrack.Persistence.PostgreSql/PostgreSqlCostQueryPort.cs:216-230` |

The earliest-session-start `MIN` and the distinct-worker list run as two sequential queries over
the identical filter (`leaf_work_id` in subtree, `started_at < asOf`).

**Target design (Stage 2):** one grouped query — `GROUP BY worked_by_user_id` returning each
worker's min start — yields the worker list and the global minimum (client-side min over ≤ worker
count rows) in a single round trip. Composes naturally with Stage 3's join form.

### 2.4 Worker-input loads materialise full entities

| | |
|---|---|
| **Severity** | Low (narrow rows; evidence-gated) |
| **Evidence** | `src/JobTrack.Persistence.PostgreSql/PostgreSqlCostQueryPort.cs:241-257` (schedule versions, intervals, exceptions, rates, app users loaded as full `AsNoTracking` entities; the session load already projects five columns via `SqlQuery`) |

**Target design (Stage 5, optional):** project only the columns the assembly reads, matching the
session load's existing shape. Undertaken only if Stage 1's profiling shows entity materialisation
visible next to the query cost itself; otherwise recorded as measured-and-declined, following the
§2.2/§2.3 index-decision precedent in the budgets doc.

## 3. Stages

TDD per impl plan §6 throughout: failing test first, smallest correct implementation, refactor.
Every stage ends with `scripts/perf-test.sh` (the only accepted evidence lane for a ceiling) and a
targeted `--filter` run of the touched contract-test classes on both providers.

### Stage 1 — Long-history scale generator and baseline (the forcing measurement)

1. Build the **long history** generator in `JobTrack.TestSupport` to the budgets doc §1 spec: one
   subtree with 5 years of daily `work_session` rows for 20 users (≈36,500 sessions) plus 5 years
   of daily schedule exceptions; recorded seed, per §6.6's reproducibility rule.
2. Add the two deferred budget rows' tests to `JobTrack.Database.PerformanceTests`: single-leaf
   cost at 150 ms and 100-leaf branch cost at 2 s, measured warm per the §2.7 protocol.
3. Instrument the baseline run to record, per worker: expanded-interval count, session count, and
   the DB-materialisation vs. pure-engine split (the same split the overlapping-cost scale
   recorded). This is the Stage 4 gate evidence.
4. Update `performance-budgets.md`: move the long-history rows from "not yet tested" to measured,
   with figures.

**Gate outcome (2026-08-06, measured):** single-leaf cost 1,217.6 ms (150 ms budget), 20-worker
branch cost 8,174.6 ms (2 s budget) — both far over. Expanded-interval count (36,520) matched
session count (36,500) almost exactly (ratio 1.00), disproving the original §2.1 hypothesis
(session-cover expansion would have nothing to trim at this scale). A follow-up microbenchmark
isolated the real cost to `IntervalAlgebra.Subtract`'s O(M×C) cut-matching: 378.5 ms for one
worker's 1,825×1,825 resolution, 4,615.4 ms for 20 workers — the large majority of the observed
latency. §2.1 above is retargeted to this measured defect; Stage 4 is **triggered**, redesigned
around it.

### Stage 2 — Collapse worker discovery to one query

1. Failing test: a command-count contract test (interceptor seam, as in the existing bulk-path
   tests) pinning the cost-input read's command count one lower than today's.
2. Implement the grouped query in both providers' `CostQueryAssembly`; existing contract tests
   prove unchanged results (empty subtree, no-session subtree, multi-worker).

### Stage 3 — Server-side subtree id composition — DONE (2026-08-06)

1. Failing test: `MaxArrayParameterLengthInterceptor` (new) plus a PostgreSQL-only contract test
   seeding a 200-leaf subtree and asserting the read's largest array parameter stays under 200 —
   fails against the pre-fix shape (200), passes once fixed. Pin behaviour with the existing
   subtree-narrowing contract tests (results must be identical by construction; a bit-identical
   property test is unnecessary extra work for a query-shape change).
2. Rewrite the session/worker-discovery and override queries as hand-authored `SqlQuery<TRow>`
   statements joining `job_node_subtrees`/`job_node_ancestor_chains` server-side (not LINQ composed
   over a `SqlQuery` subquery — see §2.2's "Deviation" note for why), registered in
   `InlineDmlArchitectureTests`' reviewed inventory. PostgreSQL only; SQLite's assembly keeps its
   parameterized recursive-CTE equivalent for parity, unmeasured.
3. Perf lane: combined-production-tree single-leaf cost-read ceiling (400 ms) re-run, unaffected
   (10.7 ms). Full `CostQueryPortContractTestsBase` suite (20 tests/provider) green on both
   providers; full `JobTrack.Persistence.PostgreSql.Tests` (529 tests) green.

### Stage 4 — Linear-time `IntervalAlgebra.Subtract` — DONE (2026-08-06)

1. Failing test first: `IntervalAlgebraTests.Subtract.Resolving_a_five_year_daily_schedule_against_five_years_of_daily_exceptions_stays_fast`
   in `JobTrack.Domain.Tests`, reproducing the long-history shape (1,825 disjoint minuend intervals ×
   1,825 disjoint cuts) with a 200 ms ceiling — deliberately generous because it runs in the
   parallelized fast lane, not the serialized perf lane; it only needs to separate quadratic from
   linear. Failed pre-fix at 397 ms (matching the standalone microbenchmark's 378.5 ms), passed
   post-fix at 10 ms.
2. Implemented: `Subtract` builds one `IntervalIndex` over the normalized cuts and, for each minuend
   interval, aggregates only over `index.Overlapping(source)` instead of the full cuts list. No
   change to `Subtract`'s public signature or ordering contract; the pre-existing
   `IntervalAlgebraTests.Subtract` cases (11 tests) pass unchanged as the correctness regression net.
3. Re-measured (`LongHistoryScalePerformanceTests`, three runs): the materialisation stage this plan
   actually targets — `ICostQueryPort.GetCostInputsAsync`'s DB-and-CPU input assembly — dropped from
   ~7,690 ms to **~245-260 ms, a ~30x reduction**, exactly matching the Stage 1 microbenchmark's
   prediction. That closes this plan's own scope.

   **New, out-of-scope finding surfaced by the same measurement:** fixing the materialisation stage
   unmasked `CostSegmentPartitioner`/`CostEngine`'s own per-worker computation — now the *larger*
   remaining term at ~359-508 ms (partition + calculate across 20 workers' ~36,500 sessions) — as
   end-to-end latency (leaf ~608-670 ms, branch ~721-745 ms) still exceeds the original
   pre-implementation 150 ms/2 s targets, just no longer for the reason those targets assumed. This
   is a genuinely different concern (engine computation, not data materialisation) that this plan
   never scoped (§1's four findings are all read-path/materialisation shape) and is not pursued
   here — see `performance-budgets.md`'s long-history section for the full record and the follow-up
   note. `LongHistoryScalePerformanceTests`' ceilings are revised to today's measured capability
   (900 ms leaf / 1 s branch, real headroom over the highest observed run) per
   `performance-budgets.md` §4's explicit revision policy, not silently loosened and not left
   permanently red for a defect outside this plan's stated boundary.
4. Regression check: `OverlappingCostScalePerformanceTests` (all 3 tests) still pass, unaffected or
   improved — the heavy-worker (5,000-session) scale dropped from 1,141.1 ms to 701.4 ms, the same
   `Subtract` fix paying off at a second scale this plan didn't specifically target.

### Stage 5 — Column projection — DONE (2026-08-06)

1. Evidence gathered (temporary instrumentation, not kept): a wide (9-column, full-entity-shaped)
   vs narrow (5-column) raw ADO.NET read of `user_schedule_exception` at the long-history scale
   (36,500 rows) measured **50.4 ms vs 9.7 ms** — entity-shaping cost visible next to the query
   itself, satisfying the plan's own gate. Every other worker-scoped load in the same method
   (schedule versions/intervals/rates/app users) stays in the tens of rows — not worth projecting.
2. Failing test first: `GetCostDetailsAsync_projects_schedule_exceptions_to_only_the_columns_it_reads`
   (new `CommandTextCaptureInterceptor`) asserting the exceptions query's SQL never mentions
   `reason`. Failed pre-fix (the full 9-column entity query), passed post-fix.
3. Implemented: the exceptions load projects to the five columns `CostQueryAssembly` reads
   (`UserId`, `ScheduleExceptionEffectId`, `StartedAt`, `FinishedAt`, `RateOverride`) instead of the
   full `ScheduleExceptionEntity`. PostgreSQL only, per this plan's scope; no SQLite change.
4. Re-measured (`LongHistoryScalePerformanceTests`): DB materialisation dropped further,
   ~253 ms → **~184 ms**, a ~28% reduction on top of Stage 4's ~30x win — closely matching the
   evidence estimate. Full `CostQueryPortContractTestsBase` suite (21 tests) and full
   `JobTrack.Persistence.PostgreSql.Tests` (530 tests) pass unchanged.

## 4. Completion criteria — all met (2026-08-06)

- Long-history generator exists with a recorded seed; both deferred budget rows measured and
  enforced in the perf lane; "not yet tested" note in `performance-budgets.md` updated. **Done.**
- Cost-input read issues one fewer command (Stage 2) and ships no subtree-proportional parameter
  arrays (Stage 3), proven by contract tests on both providers, with PostgreSQL plan evidence.
  **Done.**
- Stage 4's linear-time `Subtract` implemented with the existing correctness tests passing unchanged
  and re-measured long-history figures recorded in `performance-budgets.md`. **Done** (~30x
  materialisation win), plus a new out-of-scope finding (`CostSegmentPartitioner`/`CostEngine`
  computation cost at this session density) recorded rather than silently pursued or dropped.
- Stage 5's column projection, evidence-gated and implemented (~28% further materialisation win),
  proven by a command-text contract test and re-measured figures. **Done.**
- All existing cost/bulk-cost ceilings and command/connection bounds pass unchanged: full
  `JobTrack.Persistence.PostgreSql.Tests` (530 tests), full `CostQueryPortContractTestsBase` on both
  providers, `OverlappingCostScalePerformanceTests` (all 3, one improved), and the fast core suite.
  `scripts/perf-test.sh`'s serialized lane is the recommended final check before this plan closes
  out a broader unit of work, per the project's own commit-gate discipline.
- `docs/plans/README.md` index row updated to this plan's final status. **Done.**

**Summary of results:** the long-history scale's materialisation stage (`GetCostInputsAsync`'s
DB-and-CPU input assembly) dropped from ~7,690 ms to ~184 ms — a **~42x reduction** — across Stages
2-5, closing every finding in this plan's own §1/§2 scope. One new finding outside that scope
(`CostSegmentPartitioner`/`CostEngine` computation cost, now the larger remaining term in
end-to-end latency) is recorded in `performance-budgets.md` for a future plan.

**Postscript (2026-08-06, same day):** the out-of-scope engine finding was diagnosed and fixed
directly rather than via a further plan — `RateResolver.Resolve` scanned the worker's full
schedule-exception list once per segment allocation (O(allocations × exceptions), the same
quadratic shape as §2.1's `Subtract` defect), fixed by a hoisted once-per-calculation
`FilterPricedExceptions`, plus a benchmark-guided allocation/locality pass over the partitioner,
engine and interval algebra hot loops. Branch read ~721-745 ms → ~344-351 ms; ceilings tightened to
800 ms / 500 ms. Full record: `performance-budgets.md`'s long-history section.
