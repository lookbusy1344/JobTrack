# Large-database performance plan

**Date:** 2026-08-08
**Status:** Substantially complete (2026-08-08). Stages 0, 2, 4 delivered; Stage 1 withdrawn after
its required post-change concurrent-load measurement showed a throughput and p95 regression; Stage 3 deliberately
deferred (its own trigger not met -- see `performance-budgets.md` §2); Stage 5a/5b/5e delivered,
5c/5d/5f deferred pending their remaining operating-curve, worst-case and deployment-matched
measurements; Stage 6 remains unexecuted (its trigger not
met). See `docs/traceability/performance-budgets.md` §2's dated Stage 0-5 blocks and its "Plan
close-out" block for the full completion-criteria accounting.

**Scope:** Cost-read performance as the database grows — many work sessions per worker, many
workers contributing to one subtree, and many nodes. Covers the pure cost engine
(`CostSegmentPartitioner`/`CostEngine`), the per-worker orchestration in `CostQueries`, and one
structural change to the read model (period rollups). **PostgreSQL is the target throughout**:
every measurement, budget, plan-shape assertion and optimization in this plan is PostgreSQL-only.
SQLite stays functionally correct through the existing shared contract suites — dual-provider
parity is a house rule — but receives no measurement, no tuning, and no latency claim.

**Supersedes:** `2026-08-06-post-1.0-improvement-plan.md` Stage 4 ("Cost-engine computation cost"),
which recorded the finding and sketched the approach. This plan is that stage expanded, plus the
structural work Stage 4 did not scope. When this plan is adopted, mark post-1.0 Stage 4 as
delegated here rather than restating it in two places.

**Non-goals:** feature work, SQLite performance, multi-instance deployment (its own blocked plan),
and column-type work (`2026-07-11-postgresql-column-type-remediation-plan.md`, sequenced *after*
this plan per post-1.0 Stage 5's own reasoning).

---

## 1. Why this plan exists

The 2026-08-06 cost-read materialisation plan cut `GetCostInputsAsync`'s DB-and-CPU input assembly
from ~7,690 ms to ~184 ms at the long-history scale — a ~42x win that closed every finding in its
own scope. Doing so **unmasked a larger term underneath**: the pure engine's own partition-and-
calculate pass, measured at ~359–508 ms across 20 workers' ~36,500 sessions. That plan explicitly
declined to pursue it ("a genuinely different concern... left for a future plan"). This is that
plan, widened to also address the axis that makes the problem grow rather than merely exist.

The distinction that motivates the widening: engine tuning makes a lifetime-history question
cheaper by a constant factor. It does not stop that question getting more expensive as its requested
history window grows. A recent leaf whose first scoped session is recent remains naturally bounded;
an old branch or lifetime total does not. §3 identifies this unbounded axis, and §4's Stage 3 is the
only stage that can flatten it for aggregate reads.

---

## 2. Self-contained background

Everything a reader needs to evaluate this plan, without opening another document.

### 2.1 How a cost read works today

`CostQueries.CalculateAsync` (`src/JobTrack.Application/CostQueries.cs:121`) runs five steps:

1. **Authorize** — `GetCostAccessInputsAsync`, then `CostAccessPolicy.CanView`.
2. **Materialize inputs** — one `ICostQueryPort.GetCostInputsAsync` call returns a
   `CostQueryResult`: the node dictionary, the costed `Bounds`, and a `WorkerCostInputs` per
   contributing worker (their sessions, effective working intervals, scheduled working intervals,
   schedule exceptions, node rate overrides, user cost rates, default rate).
3. **Partition, per worker, sequentially** — `CostSegmentPartitioner.PartitionBounded` intersects
   each session with the working set, collects every boundary instant (session edges, rate edges,
   override edges on the node *and every ancestor*, exception edges), then sweeps the boundary list
   emitting one `SessionSegmentAllocation` per (segment × active session) with an exact
   `AllocatedShare(segmentTicks, N)`.
4. **Price, per worker** — `CostEngine.ComputeLeafCosts` (or `Calculate` when a trace is wanted)
   resolves a rate per allocation via `RateResolver.Resolve` and sums exact `decimal` contributions
   per leaf.
5. **Aggregate and merge** — hierarchy roll-up per worker, then merged into the cross-worker
   dictionaries.

Two structural facts govern everything below:

- **ADR 0017 forces database-wide reads over the costed window, not unconditional lifetime
  reads.** `GetCostInputsAsync` starts the window at the earliest session under the requested
  subtree and loads every contributing worker's session overlapping that window, including sessions
  on nodes the caller cannot see. An old branch can therefore pull lifetime-scale history; a recent
  leaf whose first scoped session is recent need not. The overlapping-cost fixture's leaf and branch
  happen to cover near-identical windows, which explains their near-identical figures without making
  that equivalence universal.
- **`N` is per-worker.** It depends only on that worker's own sessions. No cross-worker
  coordination is required to compute it. Stage 3 depends entirely on this property.

### 2.2 The two scales that matter here

Defined in `docs/traceability/performance-budgets.md` §1; restated so this plan stands alone.

| Scale | Definition | What it stresses |
|---|---|---|
| **Long history** | One subtree, 20 workers, 5 years of daily `work_session` rows (≈36,500 sessions) plus 5 years of daily `user_schedule_exception` rows, each worker on a 24×7 schedule spanning the window | Session *density* per worker over a long window — the ageing-installation case |
| **Overlapping-cost scale** | 50 workers × 400 leaves (20,000 nodes), a per-worker 6-deep sliding-window session staircase, 24×7 schedules, a 3-edge per-worker rate timeline, ~1 forward prerequisite edge per adjacent leaf pair; plus an optional 51st "heavy" worker with 5,000 sessions | Realistic per-worker session counts and concurrency depth; the heavy worker bounds the partitioner's O(P²) tail |

These tell **opposite stories**, which is why a single verdict on "is the engine slow" is wrong:

| | Engine share of end-to-end | Verdict |
|---|---|---|
| Overlapping-cost scale, branch (400 leaves, 1 worker) | 14.6 ms of 52.6 ms (~28%); DB 38.6 ms | Engine is *not* the bottleneck; 27× headroom under budget |
| Long-history scale, synthetic trace-producing engine profile (20 workers, 36,500 sessions) | ~359–508 ms | Engine is material, but Stage 0 must attribute the real aggregate-only branch path before calling it dominant |

The likely difference is sessions-per-worker rather than node count, but the existing profile calls
`Partition` + trace-producing `CostEngine.Calculate`; the real branch operation calls
`PartitionBounded` + `ComputeLeafCosts` + duration aggregation. Stage 0 corrects that apples-to-
oranges comparison before later work is selected. This plan targets the long-history profile; any
change must not regress the overlapping-cost one.

### 2.3 Current measured position (2026-08-06)

| Operation | Scale | Measured | Budget |
|---|---|---|---|
| Leaf cost details with trace, single `asOf` | Long history | ~586–658 ms, currently measured as the first/cold call | 800 ms (revised from an original 150 ms aspiration) |
| Branch cost (20 workers) | Long history | ~344–356 ms | 500 ms (revised from 2 s) |
| Leaf cost | Overlapping-cost | 95.8 ms | 150 ms |
| Branch cost (400 leaves) | Overlapping-cost | 52.6 ms | 2 s |
| Heavy worker (5,000 sessions) | Overlapping-cost | 701.4 ms | none assigned |

The 800 ms/500 ms long-history budgets were **honestly revised** to measured capability pending
this work, per `performance-budgets.md` §4's revision policy — not silently loosened. The leaf
measurement currently pays first-call process warm-up and produces a full session-level trace;
Stage 0 separates those terms before deciding whether restoring the 150 ms aspiration is meaningful.

### 2.4 Already fixed — do not redo

Each was a real defect with a recorded measurement; re-proposing them wastes a cycle.

- **`IntervalAlgebra.Subtract` O(M×C)** → now `IntervalIndex`-backed. Materialisation ~7,690 ms →
  ~245 ms; heavy worker 1,141 ms → 701 ms.
- **`RateResolver` scanning the full exception list per resolution** → `FilterPricedExceptions`
  hoisted once per calculation. Exact `(NodeId, Segment.Start)` memoization is not a remaining
  opportunity: same-worker/same-leaf overlap is forbidden, so that key is effectively unique per
  allocation. Stage 2 instead considers indexed or cursor-based timeline resolution if profiling
  shows the remaining ancestor/rate walk matters.
- **Per-instant dictionaries and the boundary `SortedSet<Instant>`** in `CostSegmentPartitioner` →
  flat sorted arrays (`:100-112`, `:196`). (Note: the *active-set* `SortedSet<int>` at `:116`
  survived this pass and is still open — Stage 2.)
- **LINQ `GroupBy`/`OrderBy` chains in `CostEngine.Calculate`** → single-pass grouping, in-place
  sort, no-clone trace-narrowing fast path.
- **Non-sargable session predicate** → `worker_overlapping_sessions` with `tstzrange &&` against
  `work_session_user_range_gist_idx`, plus post-seed `ANALYZE`. Narrow window: 0.31 ms → 0.009 ms.
- **Schedule-exception column projection** → 5 columns not 9. 50.4 ms → 9.7 ms.
- **`job_node_blocked()` per-edge fan-out** → `MATERIALIZED` CTEs. 46.7 ms → 3.9 ms.

---

## 3. The scaling axes

| Axis | Bounded today? | By what |
|---|---|---|
| Subtree node count | Yes | `MaxHierarchyNodeCount`; ADR 0039 Browse caps |
| Trace segments returned | Yes | `maxTraceSegments`, threaded into `PartitionBounded` |
| Bulk pricing width | Yes | `MaxBulkNodeIdCount`; one snapshot, ≤16 commands, 1 connection |
| Awaiting Progress candidates | Yes | ADR 0052 paging/exclusion in the query port's SQL |
| Working intervals per worker | Grows with window | Mitigated — `IntervalIndex` binary search, not linear scan |
| Contributing workers per subtree | Grows with breadth | **Unmitigated but parallelisable** — Stage 1 |
| **Sessions per contributing worker inside the requested cost window** | **No** | **Nothing. Grows with the age/span of a lifetime-style question.** |

Only the last row is genuinely unbounded. ADR 0017 multiplies its effect by loading every relevant
worker's database-wide overlaps inside the requested window, but the window starts at the earliest
scoped session. Old branches and lifetime totals age with the installation; naturally recent leaves
do not.

Stages 0–2 make that curve *shallower*. **Only Stage 3 makes it flat.**

---

## 4. Stages

Ordered by value per unit of risk. Stage 0 gates everything and includes the concurrent-load
baseline needed before Stage 1 can choose any parallelism. Stage 2 is independent constant-factor
work. Stage 3 is the largest commitment and applies only to aggregate reads; Stage 4 is small and
opportunistic; Stage 5 supplies write-path and production-growth evidence. Stage 6 is held in
reserve.

### Stage 0 — Attribution profile (gate for every later stage)

House discipline is budgets before tuning (impl plan §9.3). We know the engine costs ~359–508 ms at
the long-history scale; we do **not** know how that splits.

1. Instrument in-process (temporary, not committed — as Stage 5 of the materialisation plan did)
   and attribute the real application paths separately:
   - `GetCostDetailsAsync`: bounded partition plus trace-producing `CostEngine.Calculate`;
   - `GetHierarchyTotalsAsync`: bounded partition, leaf cost and duration aggregation, no trace;
   - `GetBulkNodeCostsAsync`: combined-leaf aggregate path;
   - `GetRequesterVisibleHierarchyAsync`: duration-only path.
2. Within each path attribute `EligiblePieces`, `Boundaries`, the boundary sweep (including
   `SortedSet` operations specifically), rate resolution, `decimal` division, duration arithmetic,
   hierarchy aggregation, trace assembly where applicable, and cross-worker merge.
3. Record allocation count, peak live/working-set bytes, LOH/Gen0/Gen2 bytes, wall time, and the
   DB round-trip count per operation — a per-worker N+1 query shape is a scale defect that
   aggregate timings can hide. Gen0 alone can hide a large retained trace or allocation array.
4. Warm the process and each query shape before the steady-state measurement. Record cold first-call
   latency separately rather than charging it to the engine.
5. Record the same attribution for the **overlapping-cost** scale, so no later change is accepted
   that helps one profile and regresses the other.
6. Before choosing per-request CPU parallelism, record p50/p95 latency and throughput for realistic
   simultaneous callers at the long-history scale, plus process CPU, thread-pool queueing, Cloud
   SQL connection-pool headroom, and **peak process memory under that load**. Per-request input
   materialization grows on the same unbounded axis as §3's last row and multiplies with
   concurrency; on a small Cloud Run revision, memory can be the binding limit before latency is.
   A single-caller speed-up that reduces total throughput is a regression.
7. Write the attribution and concurrent-load baseline into `performance-budgets.md` as a dated block.

**Acceptance:** operation-shaped attribution recorded for both scales, warm and cold separated,
with allocation/retained-memory figures and a concurrent-load baseline. No code change ships in
Stages 1–6 without a line in this profile justifying it.

### Stage 1 — Guarded per-worker parallelism

Candidate breadth-axis optimization, not an assumed win. Stage 0's concurrent-load baseline is a
hard prerequisite because per-request parallelism multiplies with simultaneous requests.

`CostQueries.cs:92`, `:139` and `:333` all iterate `inputs.Workers` sequentially. Each iteration —
partition, price, aggregate — reads only that worker's own inputs plus the shared immutable
`nodesById`, and touches shared state only at the final merge into `exactCosts`/`allocatedDurations`.
That is embarrassingly parallel by construction, and ADR 0017's per-worker `N` is what makes it so.

1. Failing correctness tests first: deterministic worker-order merge, cancellation, exception
   propagation, and the threshold below which execution remains sequential. Do not make a
   machine-dependent latency failure the sole red phase.
2. Parallelise per-worker compute over the already-materialized `inputs.Workers` (the DB read has
   completed; no async work is being parallelised, no connection is shared). Merge single-threaded
   after the fan-in, or per-worker into thread-local dictionaries merged at the end.
3. **Determinism is non-negotiable.** `decimal` addition is exact here, but the merge must still
   produce byte-identical output regardless of completion order — merge in a deterministic worker
   order, not in completion order. The existing engine correctness suites, including largest-
   remainder reconciliation (ADR 0002), must pass unchanged.
4. Bound the degree of parallelism explicitly through deployment configuration, capped by the CPU
   actually available to the process. A named hard-coded per-request constant is insufficient when
   request concurrency changes. If Stage 0 shows material oversubscription, use a process-wide CPU
   bulkhead so ten requests cannot each create their own maximum fan-out.
5. Confirm no regression at the overlapping-cost scale, where per-worker work is small enough that
   scheduling overhead could plausibly dominate. If it regresses there, gate parallelism on a
   worker-count or session-count threshold rather than applying it unconditionally.

**Acceptance:** long-history latency improves without reducing concurrent throughput; overlapping-
cost ceilings are unchanged or improved; correctness suites pass unchanged; sequential threshold,
deployment cap and any process-wide bulkhead have measured rationale. Withdraw the stage if the
concurrent-load result is neutral or negative.

### Stage 2 — Engine hot-path work

Constant-factor work, individually small, all pointed at by §2.4's own comments. Each item ships
only if Stage 0's profile shows it matters — this is a candidate list, not a mandate.

**2a. Fuse the aggregate-only sweep.** `GetHierarchyTotalsAsync`, `GetBulkNodeCostsAsync` and
`GetRequesterVisibleHierarchyAsync` do not expose `SessionSegmentAllocation` or a segment trace, yet
today they materialize the full allocation list, iterate it for cost, iterate it again for duration,
build per-worker hierarchy maps, and merge them. Add an internal aggregate path that emits each
eligible share directly into per-leaf exact-cost and exact-duration accumulators during the boundary
sweep. Aggregate the combined leaf maps through the hierarchy once after all workers. Keep the
existing public partition/trace path unchanged. This removes the highest-cardinality intermediate
from the operations most suitable for rollups and may make the public struct break in 2e unnecessary.

**2b. Replace the active-set `SortedSet<int>`** (`CostSegmentPartitioner.cs:116`). It is a
red-black tree: a heap node per insert, pointer-chased enumeration, and it is enumerated once per
boundary at `:136`. The same file already replaced its *other* tree and dictionary structures with
flat arrays for exactly this reason (`:95-99`, `:193-195`); this one was missed. Replace with the
packed-array + sparse-slot pattern (`int[] active` plus `int[] slotOf`, O(1) swap-remove),
giving contiguous iteration and zero steady-state allocation.

> **Risk to verify, not assume:** swap-remove changes active-index iteration order, hence allocation
> emission order. `CostEngine.Calculate` re-sorts the trace under a stated total order (`:134`) and
> sorts each segment's session list (`:105`), which *appears* to absorb it — but that must be proven
> against the golden-scenario and property tests, not assumed. If it does not hold, sort the packed
> span per segment; `N` is small and the sort is cheap.

**2c. Compile or cursor rate timelines.** Exact `(NodeId, Segment.Start)` memoization has no useful
locality: a worker cannot have overlapping sessions on the same leaf, and consecutive segments have
different starts. If Stage 0 still shows rate resolution material, sort the non-overlapping user-rate
and priced-exception timelines and resolve with binary search or monotonic cursors; alternatively
precompile effective rate intervals per worked leaf, caching a result together with the interval for
which it remains valid. Prove identical nearest-ancestor precedence across override start/end edges.

**2d. Single-probe dictionary access.** `ComputeLeafCosts:67-68` is `GetValueOrDefault` + indexer
set — two hash probes per allocation. Same shape at `CostEngine.cs:150`, `:95-97`, and
`AllocatedDurationCalculator.cs:17-18`. `CollectionsMarshal.GetValueRefOrAddDefault` halves it with
no `unsafe` and no custom collection type.

**2e. `SessionSegmentAllocation` as a `readonly record struct`, trace path only if still
justified.** It is the highest-cardinality object in the system — one heap object per (segment ×
active session), immutable and without identity. This is a public-surface change to
`JobTrack.Domain` (`PublicAPI.Shipped.txt:132-139`), but ADR 0065 supersedes ADR 0013's compatibility
gate: update the baseline and every in-repo consumer in the same change without separate breaking-
change ceremony. Still review the resulting type against the FDG as good practice and measure its
actual object/array footprint rather than relying on a hand-estimated field size.

`CostSegmentTrace` is deliberately **excluded**: eight fields makes a fat struct and
`Array.Sort` with a `Comparison<T>` copies it repeatedly. If Stage 0 shows it matters, sort an index
permutation instead — the pattern already in use at `CostSegmentPartitioner.cs:111`.

Do not take this public API break merely to optimize aggregate reads if 2a removes their allocation
objects altogether.

**2f. Struct enumerator for `IntervalIndex.Overlapping`.** It is a `yield return` iterator called
per session from `EligiblePieces:173` — one state-machine allocation per session, 36,500 of them at
the long-history scale. Minor, but free.

**2g. Pool large transient buffers.** The sweep materializes buffers sized by session and boundary
count — past the 85,000-byte LOH threshold well before the 36,500-session scale, and a fresh LOH
allocation per request under concurrent load is Gen2 pressure by another name. If Stage 0's
LOH/Gen2 figures show churn, introduce a small internal scratch-buffer owner over `ArrayPool<T>` for
the boundary, eligible-piece and — where 2a leaves it alive — allocation buffers. Do not try to hide
a rented backing array behind `List<T>`, which has no supported constructor for one. The owner must:

- return every buffer on success, cancellation and exception;
- clear every used region containing references before return, so pooled buffers retain neither
  session graphs nor identifiers;
- never expose a pooled buffer through a returned collection or beyond the calculation lifetime;
- retain logical counts separately from the pool's potentially oversized rented arrays.

Only where the profile shows it: pooling that saves nothing is complexity with a lifetime-bug
surface.

**Acceptance per item:** Stage 0 profile line justifying it; failing test or benchmark first;
correctness suites pass **unchanged** (they are the specification — never weakened, never deleted);
re-measured figure recorded. Items that do not pay for themselves are withdrawn with evidence.

### Stage 3 — Per-worker period rollups (the structural fix)

The only stage that flattens the unbounded axis. Larger than the rest combined; sequenced after
Stage 0 so it is justified by profile, and after Stages 1–2 so it is built on a clean baseline.

**Applicability.** Because `N` is per-worker (§2.1), a rollup keyed `(worker, leaf, period)` can be
self-contained without cross-worker calculation. It can accelerate aggregate-only hierarchy, bulk
and requester-duration reads. It **cannot** answer `GetCostDetailsAsync`: that contract exposes
session-level segment boundaries, active session ids, rate sources and contributions which an
aggregate rollup cannot reconstruct. Trace-producing reads bypass rollups unless a separate product
decision later bounds the trace to a recent window. The existing 150 ms leaf-details aspiration is
therefore not a post-rollup acceptance target by default.

**Period and read shape.** Use canonical half-open UTC calendar months `[start, end)`; the period is
a storage bucket, not a viewing-zone display concept. Persist exact per-leaf results for fully
covered periods. For an arbitrary historical `asOf`, sum only complete periods ending at or before
`asOf` and run the live engine for any partial first/last period. A read carries an explicit coverage
watermark/generation so a partial backfill cannot create a gap or double count. "Closed" means fully
covered by a valid rollup, not immutable: historical corrections remain legal.

**Coverage must bound the input query, not just skip compute.** Flat latency fails if the
rollup-aware read still calls `GetCostInputsAsync` over the full costed window: the DB
materialization term (sessions, working intervals, exceptions — already ~184–245 ms at the
long-history scale) would keep growing with history even with every engine pass skipped. The read
path must first resolve rollup coverage, then load **every raw cost input only for the uncovered
periods** (the live tail plus any dirty gaps): sessions overlapping those ranges, schedule versions
and intervals, exceptions, user rates, node/ancestor overrides and the ancestry needed by those
sessions. Persisted generation/dirty markers validate covered rollups; reloading whole-window rate
or override timelines neither validates them nor stays flat as those timelines grow.

Worker discovery combines workers represented by valid rollups with workers contributing scoped
sessions in uncovered ranges. Multiple dirty gaps are loaded through one set-based range input, not
one command per worker or period. Telemetry records outstanding dirty-period count and oldest dirty
period; the rebuild path prioritizes them, and the read path has a measured maximum dirty-gap count
beyond which it uses the explicit full-recompute fallback rather than issuing unbounded commands.

The differential test asserts equality of *results*; separate command-count and plan-shape/query
assertions (as the GiST regression test does) prove the narrowed, set-based input read, because a
correct-but-full-window implementation would pass the differential test while silently voiding the
stage's point.

**Exactness is the hard constraint, not an afterthought:**

- Money is `decimal` (ADR 0009) → `numeric` column. Straightforward.
- `AllocatedDuration` is a **GCD-reduced `BigInteger` rational** (`TickNumerator`/`Denominator`,
  `AllocatedDuration.cs:33-39`), not a fixed-width value. Persisting it exactly needs two
  integral `numeric` columns, and the domain type currently exposes neither member publicly.
  Resolve this explicitly in the stage — either widen the type's contract or add an exact
  round-trip conversion — and **never** by storing rounded hours. Rounding here would silently
  break ADR 0002's largest-remainder reconciliation at the reporting boundary. Constrain the
  denominator positive and the zero representation canonical; test the actual Npgsql/EF
  `BigInteger` round trip and a high-concurrency denominator rather than assuming PostgreSQL
  `numeric` is operationally unbounded.

**Invalidation surface.** A period's rollup for a worker becomes dirty when, for that worker:

1. A session in the period is created, edited, or backdated (ADR 0003 makes historical correction
   explicit — this *must* be handled, not assumed away).
2. A `user_cost_rate` edge moves into the period, or the worker's non-effective-dated default hourly
   rate changes (which can affect every historical period lacking a more specific rate).
3. A node rate override changes on any ancestor of a node they worked, including correction from
   one node to another (both old and new descendant scopes).
4. A schedule version or exception change alters their working intervals in it.
5. A node/subtree containing worked leaves moves under a different ancestor, through either the job
   or requester command path; current hierarchy determines nearest-ancestor rate resolution for
   historical sessions.
6. **An administrator deletes a subtree containing sessions they worked (ADR 0061.)** Deletion
   changes `N` for the worker's overlapping sessions on *every* surviving leaf, not merely leaves in
   the deleted subtree. Archiving alone retains sessions and hierarchy and is not an invalidator
   unless cost inclusion semantics are separately changed to exclude archived data.

Trigger 6 is easy to under-scope, and it is a direct tension with an accepted decision, not
an implementation detail. ADR 0061 §"Two consequences" states that cost figures "compute from
current `work_session` rows and **never snapshot a report**" — and accepts that destroying a
subtree's sessions retroactively moves every surviving ancestor's reported cost. A rollup is a
cache of that current-data result. Stage 3 must dirty every affected worker-period on recursive
delete; otherwise it silently contradicts the ADR. Differential tests cover deletion, backdating,
default-rate correction, override relocation and subtree move.

**Bounded transactional invalidation.** Do not eagerly delete every `(worker, leaf, period)` row in
the causal write transaction. A historical root override or subtree deletion could otherwise turn a
small correction into descendants × workers × months write amplification. Instead, each causal
write records compact dirty-generation markers in the **same ACID transaction**. Readers use only
rollups whose generation is valid; dirty or absent periods fall back to exact recomputation. The
mechanism must be a shared persistence primitive usable by every relevant command port: rate,
employee-default-rate, session, job move, requester move and subtree deletion do not all currently
flow through one `JobNodeWriteExceptionTranslation` helper.

Choose the least broad marker shape proven correct. `(worker, period)` is the safe baseline because
session changes alter `N` across unrelated leaves. A selective `(worker, subtree-root, period)`
marker may reduce override/move rebuild work, but must not be generalized to session deletion.

**Rebuild, backfill and races.** The schema migration adds empty cache/validity structures only.
Historical backfill is resumable, idempotent and outside the migration transaction, with bounded
batches and a recorded high-water mark. Replacing a rollup and clearing its dirty marker commit
atomically. A concurrency test covers all three orderings of causal write, rebuild and read, proving
that a read sees either the old source-consistent snapshot or the new one, never stale rollup plus
new tail, a gap, or double counting.

**Rollback and shadow verification.** Rollups are a cache on the money path, so the stage ships
with two safety properties, not just a passing test suite:

- A configuration switch that bypasses rollups entirely and recomputes from sessions. If a
  production discrepancy is ever suspected, the fallback is a setting, not a deploy.
- A verification mode that recomputes a sampled node from scratch and compares against
  rollup-plus-tail, logging any mismatch as an error. Run it against the UAT seed and enable it
  briefly on first production rollout. Cheap to build while the differential test harness is
  already in hand; expensive to retrofit after a silent divergence.

**Correctness proof.** A differential test on the long-history and overlapping-cost fixtures
asserts `valid rollups + live partial periods == full recompute`, exactly, for every node and several
historical `asOf` instants, including an instant inside a cached month. Repeat after every invalidator
above. Trace reads assert that they bypass aggregate rollups and remain byte-identical. This test is
the stage's acceptance gate; the optimization does not ship without it green.

**Schema.** ADR 0011 is now binding (post-1.0), so this is a **new forward-only versioned script**
in `database/postgresql/schema-versions/`, expand/contract compatible with the revision serving
traffic — not an in-place edit of an existing script. SQLite gets the parity schema and passes the
shared contract tests; it is not measured.

Do not settle the table shape before the applicability, generation, backfill and invalidation model
above is accepted. Forward-only migration makes a premature shape more expensive, not safer.

**Session archival remains separate.** Rollups alone do not make source sessions disposable.
Historical corrections, default/rate changes, hierarchy moves, fallback recomputation and shadow
verification can invalidate old rollups later. Deleting the source would then make rebuild and
verification impossible. Any future archival plan requires either an ADR making old periods
immutable, queryable cold storage containing the raw facts, or a richer immutable fact store from
which the exact result can be rebuilt. It is not a consequence of this stage.

**When to trigger this stage.** Stages 0–2 buy a constant factor and may well be sufficient for
years. Rather than guessing, derive the trigger from production data — see Stage 5's telemetry
item. First measure aggregate-read curves at 1, 5 and 10 years. Schedule this stage when aggregate-
only p95 cost-read latency exceeds half its budget or production window/session telemetry crosses
the measured knee in that curve. Do not infer a ~2,000-session knee by interpolating between two
fixtures with different shapes. Until then, Stage 3 stays deliberately unbuilt and no cache schema
is committed.

**Acceptance:** differential exactness is green for every invalidator and historical `asOf` shape;
dirty marking is transactional and bounded; rebuild/read/write races are proven; partial backfill is
safe; the covered-period input read is proven narrowed by a plan-shape assertion, not only by the
differential result; command-count coverage proves multiple dirty gaps remain set-based and every
raw input is range-bounded; outstanding dirty-gap telemetry and the full-recompute threshold are
documented; aggregate hierarchy/bulk/requester-duration ceilings tighten to post-rollup capability;
trace reads remain correct and are explicitly excluded from the flat-latency claim; aggregate read
latency is measured at 1, 5 and 10 years and shown flat after cache coverage.

### Stage 4 — Bounded-window reads

Small, independent, opportunistic. First audit current call sites: the existing port already starts
at the earliest scoped-subtree session, and the public cost requests expose only `asOf`, not a start
bound. Do not invent a monthly/date-ranged feature under this performance plan. If a current read's
answer is genuinely period-scoped, pass real bounds and collect what the GiST fix already
demonstrated: 0.31 ms → 0.009 ms, 89 → 4 block reads on a 5,000-session worker. Lifetime totals
cannot narrow this way, which is precisely what Stage 3 covers for aggregate reads.

**Acceptance:** each converted call site named, with a before/after measurement and a test asserting
the narrower window is actually used (plan-shape assertion, as the existing GiST regression test
does).

### Stage 5 — Write path, operations, and the growth signal

The request path is only part of a multi-year database's operating envelope. Stage 0 owns the
concurrent-read baseline because it gates Stage 1; this stage covers writes, observability,
storage maintenance and deployment configuration.

**5a. Write-path scaling.** `WriteContentionPerformanceTests` covers contention (concurrent session
starts, move-under-advisory-lock), but confirm coverage of write *scaling* at long-history density —
in particular that starting a session still rejects same-user/same-leaf overlap in bounded time when
the worker already has ~36,500 sessions, and that reopen-driven `job_node_blocked` recomputation
(ADR 0051) stays bounded. Reads are not the only thing a growing table slows down; if coverage
already exists, record that and close the item rather than adding a redundant test.

**5b. The growth signal.** Post-1.0 Stage 2 already logs one structured line per state-changing
operation with duration. Extend the cost-read path with two dimensions that make growth visible
*before* it becomes a complaint: cost-read duration split DB-vs-engine, cost-window span, contributing
worker count, total session count, and max/p50/p95 sessions per worker. Do not log a per-worker array:
its payload grows with the operation and adds no trigger value. Session count alone is not a memory
model: working-interval, exception, boundary, ancestry and emitted-allocation counts vary
independently, especially with concurrency depth and trace inclusion. Stage 0 derives a measured
memory model from those dimensions; production logs emit only the compact subset that materially
predicts memory, while platform metrics remain the source for peak process memory under concurrent
load. Redaction rules bind — durations and counts only, never identities, rates or costs in a log
line.

**5c. PostgreSQL operating curve.** Record `work_session`/exception table and index size, autovacuum
lag/dead tuples, backup/restore time and the forward-migration window at the 5- and 10-year scales.
Evaluate declarative time partitioning, a BRIN companion index for append-heavy temporal tables,
and a covering (`INCLUDE`) variant of the worker-leading index — the wide-window five-column session
projection reads every heap page a lifetime window touches, and an index-only scan (visibility-map
health permitting) could cut its buffer count where the GiST's narrow-window advantage does not
apply. Each only against measured bounded-window, vacuum, buffer and storage benefits. None is
proposed as a cure for lifetime totals, and the existing worker-leading indexes remain
authoritative unless evidence says otherwise.

**5d. Bounded worst case.** Today a pathological lifetime read on a 10-year database has no ceiling:
it holds a pooled connection and a request thread for as long as it takes. Measure that worst case
at the 10-year scale, then evaluate an end-to-end interactive-read deadline against a transaction-
local PostgreSQL `statement_timeout`; a per-command timeout alone does not bound a multi-command
transaction. Interactive reads and rollup rebuild/full-verification work have separate budgets, so
a guardrail cannot make the exact fallback or repair path impossible. If adopted, the setting is
configuration-derived from measurement with real headroom, never leaks through a pooled connection,
maps cancellation/timeout to an explicitly documented application/API failure, and is cleared by
every budgeted perf lane. Adopt it only if the measured pool-protection benefit exceeds the failure-
surface cost; otherwise withdraw it with the worst-case figure recorded. This is an operational
guardrail, not a substitute for Stage 3.

**5e. Fixture economics.** The 1/5/10-year measurements this plan demands are only sustainable if
seeding them stays cheap. Record seed time at each scale. Only if seed cost is material relative to
the lane, compare further set-based generator optimization with a reusable seeded snapshot
(PostgreSQL template database or dump/restore, version-stamped against the schema and generator),
and adopt the lower-maintenance option on measured payback. Do not build snapshot lifecycle
machinery merely because a 10-year fixture exists, and never silently measure less. This is harness
configuration/documentation, not product behaviour and not a subject for a product test.

**5f. Runtime configuration candidates.** Three config-only comparisons, each run only when Stage
0's attribution identifies the term it could affect, on a container sized like the deployed Cloud
Run revision:

1. **GC mode** — .NET 10 already enables DATAS by default. If allocation/retained-memory profiling
   justifies it, compare the deployed default with explicitly selected Server and Workstation GC at
   the deployed vCPU count, including throughput and peak working set; DATAS is the baseline, not an
   untried switch.
2. **ReadyToRun validation** — both production Dockerfiles already restore and publish the Web image
   with `PublishReadyToRun=true`. If cold-start attribution justifies it, compare that existing
   baseline with R2R disabled, recording cold-call latency, image-size and working-set deltas. This
   validates an existing choice; it is not a new enablement step.
3. **Npgsql automatic preparation** (`Max Auto Prepare`) — the cost-input read repeats the same
   parameterized query shapes every request and the feature is disabled by default; if DB parse/plan
   time is material, compare it with the default, including prepared-statement churn across pooled
   connectors.

None changes semantics. A candidate whose target term is not material in Stage 0 is withdrawn
without running another experiment; one that is measured lands only with a before/after figure.

**Acceptance:** write-path scaling coverage confirmed or added; compact telemetry dimensions emitted
and documented as Stage 3 trigger inputs; storage/vacuum/backup figures recorded; partitioning,
BRIN and the covering index each justified by measured operational benefit or withdrawn with
evidence; the deadline/statement-timeout candidate is either adopted with its failure contract and
perf-lane proof or withdrawn with the measured worst case; seed-time figures are recorded and any
fixture optimization is adopted only on measured payback; each 5f candidate is either screened out
by Stage 0 or adopted/withdrawn with a measured before/after figure.

### Stage 6 — Reserve: server-side sweep

**Do not execute without a trigger.** If, after Stages 0–4, the profile still shows the partition
sweep dominant, the house style already sanctions a source-controlled PostgreSQL function invoked
through EF (`HasDbFunction`/`FromSql`) — same benefit an unmanaged language would offer, no FFI, no
second toolchain in the Cloud Run image, and it avoids materialising rows that exist only to be
swept.

The standing rule from impl plan §9.3 binds hard here: **no PostgreSQL-only cost algorithm** unless
differential tests prove it equivalent to the pure engine and SQLite stays conformant. The strong
default is that this never happens and the engine stays in the domain layer.

---

## 5. Rejected options

Recorded so they are not re-proposed.

- **Rust / native / FFI for the cost engine.** Assessed 2026-08-08 and rejected. At the
  overlapping-cost scale the engine is 14.6 ms of 52.6 ms — Amdahl caps the win before FFI cost
  applies. At the long-history scale the problem is a growth rate (§3), and a native rewrite buys a
  constant factor while leaving the curve's shape untouched — Stage 3 is the actual answer. Against
  that: no primitive `decimal` in Rust means re-proving ADR 0002/0009 exactness on a third-party
  decimal library, the inputs are marshalling-hostile (node dictionaries, ancestor walks, Noda
  `Instant`s), and it adds a toolchain to the deployed image.
- **`unsafe`, raw pointers, memory-mapped input, SIMD.** No parsing and no file I/O exist on this
  path; inputs arrive as materialized objects over a socket. Bounds-check elimination chases a
  percentage of a term that the fused aggregate path and rollups can remove from aggregate reads;
  trace reads remain output-cardinality-bound.
- **`double` anywhere on the duration or money path.** Prohibited by house style and ADR 0009. Not
  negotiable for performance.
- **Caching rounded values.** Breaks ADR 0002's largest-remainder reconciliation. Rollups store
  exact values or they do not ship (Stage 3).
- **In-memory result caching without persisted invalidation** (e.g. `IMemoryCache` over aggregate
  cost results with a TTL). A TTL on the money path serves stale costs by design; correct
  invalidation needs exactly the dirty-generation machinery Stage 3 builds, at which point a
  per-instance generation-keyed memory cache becomes a legal *follow-on* to Stage 3 — never a
  cheaper substitute for it. (Multi-instance deployment, if ever unblocked, invalidates the
  per-instance form; the persisted rollup does not care.)
- **Relaxing ADR 0017's elevated read scope** (e.g. computing `N` from only the visible subtree).
  This would make costs *wrong*, not fast.
- **SQLite performance work.** Out of scope by standing house rule; SQLite is correctness-only.

**Adjacent, deliberately not scoped here (recorded so they are not forgotten):** non-cost read
paths that also grow with database size — Awaiting Progress with `ExcludeBlocked=false` (measured
199.9 ms, dominated by materializing candidate dependents, not by blocked-set computation),
job search at combined-production-tree scale (ADR 0050), and Browse subtree assembly. These are
bounded by caps and paging today (§3) and none is currently a measured defect, so widening this
plan to cover them would dilute it. If any becomes a complaint, it gets its own plan or a stage
added here by amendment — not a silent expansion of scope mid-delivery.

---

## 6. Invariants no stage may break

1. Exact rational duration `(segmentTicks, N)`; `decimal` money; rounding only at the reporting
   boundary with ADR 0002 largest-remainder reconciliation.
2. ADR 0017: `N` computed from the worker's database-wide sessions; foreign session identities,
   nodes and rates never exposed to a caller scoped elsewhere.
3. Existing engine correctness suites pass **unchanged** — they are the specification. A failing
   test is never weakened or deleted to make a stage pass; if one looks wrong, ask.
4. Compound writes (Stage 3's dirty-generation marker) commit in the causal command's one ACID
   transaction through the appropriate shared persistence primitive, proven by per-slice
   concurrency tests.
5. Determinism: identical inputs produce byte-identical outputs regardless of parallel scheduling.
6. Dual-provider parity maintained; SQLite conformant via shared contract tests.

---

## 7. Delivery discipline

- One stage per commit series. Commit gate after every slice: `dotnet build JobTrack.slnx
  -warnaserror`, `dotnet format JobTrack.slnx`, `./scripts/fast-test.sh --build`, then a **targeted**
  `dotnet test --filter` scoped to the changed classes — never a full solution run mid-stage.
- `./scripts/perf-test.sh` at each stage close (the performance project is excluded from
  `dotnet test JobTrack.slnx` by `IsTestProject=false`); `./scripts/all-test.sh` at plan close.
- Every `dotnet`, `git add`/`git commit`, and `psql`/`pg_isready` call sets
  `dangerouslyDisableSandbox: true`. Wrap test invocations in `gtimeout` sized to the category.
- **Every performance claim lands as a measured figure in `performance-budgets.md` naming its
  enforcing test, or it does not land.** Ceilings are tightened to measured capability with real
  headroom, per §4's revision policy — never loosened to accommodate a regression.
- No test whose subject is the test harness.
- Update this plan's status block per stage, and the `docs/plans/README.md` row with it.

---

## 8. Completion criteria

Complete when:

1. Stage 0's operation-shaped attribution is recorded for both scales, warm/cold separated, with
   the concurrent-load baseline.
2. Stages 1–5 are each delivered or withdrawn with recorded evidence.
3. Long-history leaf and branch ceilings are tightened to post-work capability — with an explicit
   statement of whether the original 150 ms leaf aspiration was reached and, if not, the honest
   number and the reason.
4. Overlapping-cost ceilings are unchanged or improved; the heavy-worker (5,000-session) figure is
   re-measured and either given a real budget or explicitly left unbudgeted with a reason.
5. If Stage 3 shipped: aggregate-read latency is demonstrated flat in history length across 1-, 5-
   and 10-year fixtures; trace reads are explicitly excluded from that claim.
6. Stage 6 remains unexecuted, or its differential-equivalence gate is green.
7. If Stage 3 was deliberately deferred, its trigger criterion is recorded and its telemetry
   (Stage 5b) is live, so the trigger fires on data rather than on someone remembering.
8. `2026-08-06-post-1.0-improvement-plan.md` Stage 4 is annotated as delegated to this plan, and
   the plans index reflects both.

---

## 9. Relationship to other plans

| Plan | Relationship |
|---|---|
| `2026-08-06-cost-read-materialisation-reduction-plan.md` | Implemented. Its §"new finding" is this plan's §1 premise. Read it before Stage 0 — it records what has already been tried and measured. |
| `2026-08-06-post-1.0-improvement-plan.md` | Stage 4 is superseded by this plan. Its Stage 5 (column types) stays sequenced *after* this work, evaluated against post-Stage-2 profiles. |
| `2026-07-09-overlapping-cost-scale-plan.md` | Implemented. Owns the overlapping-cost fixture and generator this plan measures against. |
| `2026-07-24` / `2026-07-25` scalability plans | Implemented. Removed the structural read-path defects; this plan starts from that baseline. |
| `2026-07-11-postgresql-column-type-remediation-plan.md` | Proposed, deferred. Downstream of this plan. |
| `2026-07-26-multi-instance-web-deployment-plan.md` | Blocked, unrelated. Horizontal scaling is not a substitute for any stage here. |
