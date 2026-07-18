# Overlapping-cost scale generator and cost-calculation performance test

**Status:** Implemented
**Date:** 2026-07-09
**Owner:** database/domain performance work
**Closes:** the two deferred cost-calculation rows in
[`docs/traceability/performance-budgets.md §2`](../traceability/performance-budgets.md) and the
deferred "long history"-class generator they depend on (plan §6.6). Both were deferred during the
database phase because the cost engine did not yet exist; it now does (M6 library gate accepted,
ADR 0026; M8 web gate accepted, ADR 0027), so the deferral is spent.

---

## 1. Problem and current state

There is no dataset that exercises the cost engine at scale, and there is no cost-calculation
performance test anywhere in the suite. Concretely:

- `tests/JobTrack.TestSupport/PerformanceScaleGenerator.cs` seeds trees (deep / broad / 200k
  combined), a 100-session "high concurrency" worker, and 2,000-user rate timelines — all
  server-side `INSERT … SELECT`. **None of it drives cost.** The high-concurrency worker's 100
  sessions are all open at one instant with no rate/override/schedule variation, so it measures
  overlap *discovery*, not cost *calculation*.
- `docs/traceability/performance-budgets.md §2` defines cost budgets — **150 ms** for one leaf,
  **2 s** for a 100-leaf branch — but the doc explicitly records both, plus the "long history"
  generator, as deferred: *"need the Phase 2 cost engine … re-tested once that engine lands."*
- `JobTrack.Database.PerformanceTests` contains no cost test.

So the honest answer to "what is the performance of cost calculations?" is currently **unmeasured**.
This plan builds the dataset and the measurement.

## 2. Domain semantics this plan is built against

Read before implementing; these determine the generator's shape.

1. **Overlap depth = N, per worker.** `CostSegmentPartitioner` partitions one worker's sessions
   into maximal segments of constant active-session membership and allocates each active session
   `1/N` of the segment (`AllocatedShare(segmentTicks, N)`). N is the count of a **single worker's**
   sessions that are simultaneously open **across distinct leaves**. Overlaps never cross workers —
   each worker is costed independently (`CostQueries.CalculateAsync`, per-worker loop). This is why
   50 workers is the right knob: overlap only ever applies within one person.
2. **Same-worker/same-leaf overlap is illegal.** `CostSegmentPartitioner.ValidateNoSameLeafOverlap`
   throws `InvariantViolationException`, and the PostgreSQL GiST exclusion constraint
   `work_session_no_same_leaf_user_overlap` rejects it at insert. A 6-deep overlap must therefore
   span **6 different leaves**.
3. **Prerequisites do not feed the cost engine.** `CostQueryAssembly.LoadWorkersAsync` never reads
   `job_prerequisite`. Cost is a function of sessions, schedule/working-time, rates, overrides, and
   concurrency only. We still generate pre/post-requisite edges (§5.4) for dataset realism and to
   keep the fixture reusable for readiness/overlap-candidate benchmarks — **but they change no cost
   number**, and the plan will not assert otherwise.
4. **The partitioner is O(P²) per worker.** `CostSegmentPartitioner.Partition` re-scans every
   eligible piece inside the boundary loop (`CostSegmentPartitioner.cs:58-66`): B ≈ 2·P boundaries ×
   P pieces. The DB port materializes a worker's **entire database-wide session history** for any
   cost read (`LoadWorkersAsync` loads all sessions for every worker who touched the requested
   subtree — ADR 0017's elevated read scope for a correct N). Therefore *sessions-per-worker*, not
   *total jobs*, dominates cost latency. The dataset shape must make this visible, not hide it.

## 3. Goals / non-goals

**Goals**
- A deterministic, seed-recorded, server-side generator producing 20,000 leaf jobs across 50
  workers with controlled overlap depth up to 6, schedules, rates, and pre/post-requisite edges.
- A PostgreSQL cost-calculation performance test asserting the §2 budgets, reporting the
  DB-materialization vs pure-engine split.
- Measured numbers recorded back into `performance-budgets.md` per its "measure, don't guess"
  policy, adding an *Overlapping-cost scale* row rather than overloading "long history".

**Non-goals**
- Not building the full "long history" 5-years-daily scale (36,500 sessions/user); that scale
  targets historical `asOf`-range recalculation, a separate row. This plan builds the
  *overlap-depth* scale.
- Not changing the cost engine or the port. If measurement reveals the O(P²) partitioner or
  whole-history materialization misses budget, that is a **finding** to record here and raise
  against the domain/persistence phase — not something this plan silently patches.
- No SQLite latency budget (its single-writer envelope is exempt, §6.4); a SQLite *functional*
  smoke that the same generated scale computes cost without unbounded blocking is in scope.

## 4. The overlap-generation algorithm

**Sliding-window staircase**, per worker, deterministic and with exactly-known N — which makes the
fixture double as a *correctness oracle* (the partitioner's 1/N is checkable against the known
staircase), not merely a timing harness.

Given a worker owning ordered leaves `leaf₁ … leaf_L`, a slot duration `S`, base instant `t₀`, and
target depth `D = 6`:

```
session on leafₖ  :=  [ t₀ + (k−1)·S ,  t₀ + (k−1)·S + D·S )      for k = 1 … L
```

Each session is `D` slots long; consecutive sessions start one slot apart. At any instant in slot
`m` the active set is `{ k : m−D+1 ≤ k ≤ m } ∩ [1,L]`:

- interior slots (`D ≤ m ≤ L`): **exactly D = 6** concurrent sessions;
- ramp-up (first `D−1` slots): depth `1,2,…,5`;
- ramp-down (last `D−1` slots): depth `5,…,1`.

All concurrently-open sessions have distinct `k` ⇒ distinct leaves ⇒ **no same-leaf overlap**, so
the GiST exclusion constraint and `ValidateNoSameLeafOverlap` are both satisfied by construction.
The exact per-segment N is a closed form of `m`, so a correctness assertion is
`sum over active sessions of 1/N · segmentDuration == segmentDuration` (already a property test) plus
`N == expectedStaircaseDepth(m)` on the generated data.

**Scale parameters (chosen with the user):**

| Parameter | Value | Rationale |
|---|---|---|
| Workers | 50 | Overlap/N is per-person; 50 independent staircases. |
| Leaves (jobs) total | 20,000 | 400 leaves per worker. |
| Sessions per worker | 400 | One per owned leaf. |
| Max overlap depth `D` | 6 | Interior flat region sits at N = 6. |
| Slot `S` | 1 h (const, named) | Keeps arithmetic exact; tune to keep timeline bounded. |
| Schedule | 24×7 per worker | So every staircase slot is working time and N is exact end-to-end. Realism of *schedules* is covered by other scales; this scale isolates cost throughput. Recorded as a deliberate simplification. |
| Rate source | `app_user.default_hourly_rate` + a short `user_cost_rate` timeline (e.g. 3 edges) crossing the window | Forces at least one rate-boundary split inside the staircase so `RateResolver` and the rate-edge boundary set are actually exercised, not short-circuited. |

**Optional worst-case addendum (recommended, cheap):** add **one** extra "heavy" worker with ~5,000
sessions in the same staircase form, to bound the O(P²) tail that 400-session workers won't reveal.
This is the only way the benchmark surfaces the partitioner's quadratic term; without it the report
understates the tail. Kept as a separate seeded case so the realistic-shape number stays clean.

## 5. Generator design

Extend `PerformanceScaleGenerator` with `SeedOverlappingCostScaleAsync`, following the file's
existing discipline: server-side set-based `INSERT … SELECT`, `LockSafeBatchSize = 300` batching to
avoid `max_locks_per_transaction` exhaustion (53200), named constants for every kind/priority/depth,
and a **recorded seed** returned so a failing run reproduces exactly (plan §6.6).

Returns a small record of anchor ids the test needs: `(OwnerActorId, OneLeafId, OneBranchId,
AsOf, Seed)`.

Build order (each a bounded batched statement):

1. **Workers** — 50 `app_user` rows with `default_hourly_rate`.
2. **Hierarchy** — a root, 50 worker branches, 20,000 leaves (400 per branch) + `leaf_work` rows.
   Reuse the existing `InsertLevelAsync` / `InsertLeafWorkInBatchesAsync` helpers.
3. **Schedules** — one `schedule_version` per worker (24×7 weekly intervals) covering the window.
   *Required*: with no working intervals, `EligiblePieces` is empty and cost is zero — the benchmark
   would measure nothing. Every SQLite connection that touches this (functional smoke) still sets
   the four pragmas per CLAUDE.md.
4. **Rates** — a 3-edge `user_cost_rate` timeline per worker crossing the staircase window.
5. **Sessions** — the staircase (§4) as `work_session` rows: `generate_series` over `k`, computing
   `started_at`/`finished_at` from `k`, `S`, `D`. Server-side, batched.
6. **Prerequisites** — pre/post-requisite `job_prerequisite` edges. Acyclic **by construction**:
   only insert `(from_id, to_id)` where `from_id < to_id` in the seeded id ordering, so the
   `check_job_prerequisite_no_cycle` trigger can never fire. Note the trigger runs a recursive CTE
   **per inserted row**, so keep this to ~1–2 edges/node (≈20–40k edges) and batch. These give each
   node genuine incoming (pre) and outgoing (post) requisites without affecting cost.

An `asOf` just after the last session's end is returned so all sessions are in-window and finished.

## 6. What is measured, and where

**Recommended: end-to-end through `CostQueries`, with a component breakdown.** This answers the
literal question ("performance of cost calculations") honestly — it includes EF materialization,
which the §2 budget explicitly counts ("wall-clock … including EF materialization"). The test:

1. Seeds the scale (§5) and one actor with cost-view role (an `IdentityUserEntity` + role row —
   `PostgreSqlCostQueryPort.GetActorRolesAsync` requires it).
2. **Leaf budget (150 ms):** times `CostQueries.GetCostDetailsAsync` for one leaf, warmed pool.
3. **Branch budget (2 s):** times `CostQueries.GetHierarchyTotalsAsync` for one 100-leaf branch.
4. Separately times `ICostQueryPort.GetCostInputsAsync` (DB) vs `CostSegmentPartitioner` +
   `CostEngine` (pure) over the already-materialized inputs, and reports the split so a budget miss
   is attributable to DB vs engine.
5. Asserts a query-plan requirement mirroring the neighbouring perf tests (`PostgreSqlExplainPlan`,
   no sequential scan of `work_session` on the cost-input query).

Alternatives considered: *port + engine only* (skips actor/auth seeding — simpler but doesn't
measure the shipped entry point) and *pure engine only* (isolates the algorithm — useful as the
breakdown in step 4, insufficient as the headline number). The breakdown gives us all three, so the
end-to-end path is the right headline.

## 7. Budgets

Add to `performance-budgets.md`:

- **§1** a new scale row *Overlapping-cost scale*: 50 workers × 400 leaves (20,000 `job_node`),
  6-deep per-worker session staircase, 24×7 schedules, short per-worker rate timeline, ~1–2
  prerequisite edges/node; plus the optional one heavy 5,000-session worker.
- **§2** repoint the two cost rows at this scale (they currently name "long history", which this
  plan is *not* building) **or** add two parallel rows. The 150 ms / 2 s targets are provisional
  until the first measured run; per the doc's policy they are then **re-measured, not re-guessed**,
  and revised in place with a one-line rationale if the design can't meet them. A miss traced to the
  O(P²) partitioner or whole-history materialization is recorded as a finding against the
  domain/persistence phase, not silently loosened.

## 8. TDD sequence (mandatory, per CLAUDE.md)

1. **Generator unit test first** — assert structural invariants of the seeded scale on a small
   parameterised instance (e.g. 3 workers × 8 leaves, D = 3): row counts, that max per-worker
   concurrency equals D, that no same-leaf overlap exists, that prereq edges are acyclic. This is
   the failing test that drives `SeedOverlappingCostScaleAsync`.
2. **Correctness cross-check** — on the small instance, assert the partitioner's per-segment N
   matches the closed-form staircase depth (the oracle from §4), and allocation conservation holds.
3. **Performance test** — the §6 end-to-end test at full 50×400 scale, asserting §2 budgets. Written
   after correctness so a budget failure is unambiguously performance, not a wrong dataset.
4. **SQLite functional smoke** — the same generated scale (SQLite provider, four pragmas set)
   computes cost without unbounded blocking; no latency assertion (§6.4).
5. Record measured numbers into `performance-budgets.md`; commit through the four-command gate
   (`build -warnaserror`, `format`, `format --verify-no-changes`, `test`), every `dotnet` call with
   `dangerouslyDisableSandbox: true`, tests under a `gtimeout` sized to the DB category (not the
   reflexive short one), and drop any orphaned `jobtrack_test_*` databases afterward.

## 9. Risks and mitigations

| Risk | Mitigation |
|---|---|
| `max_locks_per_transaction` exhaustion (53200) on bulk insert | Reuse `LockSafeBatchSize = 300` batching; never one statement over tens of thousands of distinct FK parents. |
| GiST exclusion rejects generated sessions | Staircase guarantees distinct leaves per concurrent set (§4); the small-instance test proves it before scaling. |
| Prereq acyclicity trigger cost (recursive CTE per row) | Only forward `from_id < to_id` edges; cap at ~1–2/node; batch. |
| Empty working-time ⇒ zero cost ⇒ meaningless benchmark | Seed 24×7 schedule per worker (§5 step 3); step-2 correctness test would catch a zero-cost dataset. |
| 400-session workers hide the O(P²) tail | Optional heavy 5,000-session worker (§4) as a separate case. |
| Budget miss | Recorded as a finding + budget revision with rationale, per §7 — not a silent test loosening. |

## 10. Acceptance criteria

- `SeedOverlappingCostScaleAsync` seeds the §4/§5 scale deterministically from a recorded seed,
  server-side, within the lock envelope.
- Generator + correctness tests (§8.1–8.2) pass, proving max concurrency = 6, no same-leaf overlap,
  acyclic prereqs, exact per-segment N, allocation conservation.
- The PostgreSQL cost performance test runs green with recorded leaf/branch latencies and the
  DB-vs-engine breakdown; SQLite functional smoke completes without unbounded blocking.
- `performance-budgets.md` carries the new scale row and measured cost numbers.
- Four-command commit gate passes; no orphaned test databases left behind.

## 11. Open decisions (defaults chosen, flag if you disagree)

1. **Measurement surface** — default: end-to-end `CostQueries` + breakdown (§6). Alternative:
   port+engine only.
2. **Heavy-worker addendum** — default: include the one 5,000-session worker as a separate case, to
   bound the O(P²) tail. Alternative: realistic 50×400 shape only.
3. **Budget rows** — default: add a new *Overlapping-cost scale* pair rather than repointing the
   existing "long history" rows, keeping the (still-deferred) long-history scale row intact.
