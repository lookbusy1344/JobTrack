# De-risking spike report

**Closes:** Implementation plan §5.3, §5.5 exit criterion ("the de-risking spikes demonstrate the
required PostgreSQL behaviour under concurrent writes, and the time model handles the documented
DST cases deterministically").

All spike code is throwaway proof, kept under `spikes/` outside `src/`/`tests/`, isolated from the
repo's strict `Directory.Build.props`/`Directory.Packages.props` via `spikes/Directory.Build.props`.
It does not pass through the delivery gates (§6.7, §7.5, §8.7) and is not referenced by production
code. Run against a local PostgreSQL 18 instance (database `jobtrack_spike`) and .NET 10 (pinned SDK
10.0.301).

Every spike that concerns an invariant was run with **simultaneous independent psql connections**
(bash background processes synchronized with `pg_sleep`), not a single-threaded proof, per §5.3's
requirement.

## 1. Single-root invariant under concurrent bootstrap

**Files:** `spikes/sql/01-single-root.sql`, `01-single-root-concurrent-test.sh`,
`01b-single-root-count-only-counterfactual.sql`, `01b-count-only-concurrent-test.sh`.

Two independent connections raced to insert the first root into an empty table.

- **Mechanism under test:** a partial unique index (`UNIQUE ... WHERE parent_id IS NULL`) for "at
  most one root", plus a deferred constraint trigger for "at least one root" (ADR 0015).
- **Result: PASS.** Exactly one session's insert committed; the other observed
  `duplicate key value violates unique constraint "idx_job_node_single_root"` and rolled back.
- **Counterfactual (proves the unique index is load-bearing, not the trigger):** the same race
  against a schema using **only** a count-based deferred trigger for "at most one root" (no unique
  index) let **both** inserts commit, leaving 2 roots. Each transaction's `COUNT(*)` check only
  sees its own uncommitted row plus already-committed data — under MVCC snapshot isolation neither
  transaction can see the other's in-flight insert, so both checks pass independently. This
  confirms ADR 0015's design: the partial unique index is what actually resolves the race; the
  deferred trigger alone is not sufficient, no matter how it is written.

## 2. Prerequisite-cycle detection under concurrent edge insertion

**Files:** `spikes/sql/02-prerequisite-cycle.sql`, `02-cycle-concurrent-test.sh`.

Two independent connections concurrently inserted `A→B` and `B→A` — neither edge is a cycle against
already-committed data, but together they are.

- **Unlocked path** (deferred constraint trigger doing a recursive-CTE reachability check, no lock):
  **both edges committed**, leaving an actual cycle in the graph. Same MVCC blind-spot as spike 1's
  counterfactual: each trigger's reachability query doesn't see the other transaction's in-flight
  edge.
- **Locked path** (`add_prerequisite_edge_locked`, acquiring a fixed transaction-scoped advisory
  lock before the insert): the second transaction's reachability check now sees the first
  transaction's committed edge and correctly raised `invariant violation: prerequisite edge 1->2
  would create a cycle`; no cycle was created.
- **Finding, now recorded in ADR 0012:** deferred constraints alone are **not** sufficient for
  prerequisite-edge writes under concurrency — an advisory lock serializing all
  `job_prerequisite` inserts is required, not merely permitted. This is a "prerequisite-graph
  writes" lock domain, added to ADR 0012's lock-domain list on the strength of this spike.

## 3. GiST exclusion for same-user/same-leaf session overlap

**Files:** `spikes/sql/03-gist-overlap.sql`, `03-gist-overlap-concurrent-test.sh`.

Single-threaded sanity: adjacent closed sessions insert cleanly; an overlapping session is rejected
with `conflicting key value violates exclusion constraint`.

Concurrent race: two independent connections simultaneously started **open** (unbounded-upper
`tstzrange`) sessions for the same user/leaf, overlapping ranges.

- **Result: PASS** — exactly one session ended up with an open session for that (user, leaf); the
  other was rejected.
- **Finding for §7.4 error translation:** the rejected session did **not** receive a clean
  `23P01` exclusion-violation — it received `ERROR: deadlock detected` (`40P01`). PostgreSQL's GiST
  exclusion-constraint check under concurrent inserts of mutually conflicting ranges can manifest as
  a detected deadlock rather than a same-statement constraint violation. **This means the
  persistence layer's constraint-to-`JobTrackException`-category translation (plan §7.4, "a
  pre-check and a database race must produce the same public error category") must map *both*
  `23P01` and this specific deadlock shape to the same stable overlap-conflict category** — treating
  only `23P01` as "overlap conflict" and treating `40P01` generically (e.g. as a transient/retry
  case) would surface the wrong error category to the caller for this specific race. This is a
  concrete, actionable requirement for the persistence implementation, not a hypothetical.

## 4. Deterministic advisory-lock ordering for subtree moves

**Files:** `spikes/sql/04-advisory-lock-ordering.sql`, `04-advisory-lock-ordering-test.sh`.

`move_node_locked(a, b)` always acquires the numerically smaller of its two arguments' lock keys
first, regardless of argument order (ADR 0012's deterministic-ordering rule). Two connections issued
opposing-order requests — session A called `move_node_locked(10, 20)`, session B called
`move_node_locked(20, 10)` — for the same pair of nodes.

- **Result: PASS.** Both calls completed successfully via serialization (one waited briefly for the
  other); no deadlock. Confirms the ascending-key-order discipline is sufficient to avoid deadlock
  for the two-lock case ADR 0012 describes.

## 5. DST gap/fold resolution (Noda Time, pinned TZDB)

**Files:** `spikes/dst-spike/` (`.NET` console app, `NodaTime` 3.3.2, pinned locally in this
project's own `csproj` — deliberately **not** added to the shared `Directory.Packages.props`, since
the real `NodaTime` dependency and version are added by ADR 0016 when domain time modelling actually
starts).

Exercised `Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnForwardShifted)`
(the exact composition ADR 0008 pins) against:

- a spring-forward gap in `Europe/London` (2026-03-29 01:30 does not exist) and in
  `America/New_York` (2026-03-08 02:30 does not exist);
- an autumn-back fold in `Europe/London` (2026-10-25 01:30 occurs twice) and in
  `America/New_York` (2026-11-01 01:30 occurs twice); and
- a non-DST zone (UTC), confirming the same resolver needs no special-casing when there is no
  ambiguity.

**Result: PASS**, against TZDB version `2026b`. Every gap resolved to an instant at or after the
post-gap zone-interval boundary (forward-shifted, never truncated backward into the gap); every fold
resolved to the earlier of the two candidate instants. This confirms ADR 0008's frozen resolver
composition is correct and zone-agnostic before it is relied on by schema/API contracts.

## 6. Segment-based concurrent cost sweep vs. independent oracle

**Files:** `spikes/cost-sweep-spike/` (.NET console app, no external packages).

This spikes the **concurrency/allocation kernel** described in plan §7.2 items 8–9 — boundary
partitioning and exact-rational `1/N` allocation — in isolation from schedule/rate resolution, which
does not exist yet (that is Phase 2 domain work, not a Phase 0 spike). It is not the production cost
engine.

Two structurally different algorithms were run against the same generated session sets and their
results compared for **exact rational equality** (`BigInteger`-backed `Rational`, no floating point,
per ADR 0009):

1. **Boundary sweep** (the production approach): partition the timeline at every session start/end,
   compute the active-session count `N` per segment, allocate `segmentDuration / N` to each active
   session.
2. **Independent oracle** (structurally different — per-tick brute force, no boundary computation):
   for every individual tick in the timeline, count active sessions and accumulate `1/N` per active
   session per tick.

Cases: `N=2` (two overlapping sessions), `N=20` (staggered overlap), `N=120` (heavy overlap, well
past the "100+" bullet), and a disjoint-sessions control (`N=1` throughout, sanity-checking the
algorithm degenerates correctly when there is no concurrency).

- **Result: PASS for all four cases.** The two algorithms agreed exactly (not approximately) for
  every session in every case; total allocated time equalled the exact measure of the union of
  session intervals in every case (conservation); no session's allocated share exceeded its own
  duration.
- This confirms the boundary-sweep approach ADR 0009 mandates is sound at the concurrency levels the
  golden scenario catalogue requires (GS-010, GS-011), before the real engine (with schedule
  eligibility and rate resolution layered on top) is built in Phase 2. The real engine's own property
  tests and independent-oracle cross-check (plan §7.2, `TC-APP-COST-004`) subsume this spike once
  written; this spike's job was only to de-risk the boundary/rational-allocation *mechanism* early.

## 7. `ltree` vs. adjacency-list hierarchy queries

**Closes:** PostgreSQL column-type remediation plan
(`docs/plans/2026-07-11-postgresql-column-type-remediation-plan.md`) §3.2's exit rule: "Do not
adopt `ltree` unless it beats the current recursive functions on accepted performance scales and
does not materially complicate the move/prerequisite concurrency proof." Run later than spikes 1–6
(added to this report, not part of the original Phase 0 batch), but kept under the same `spikes/`
convention and numbered next in sequence.

**Files:** `spikes/sql/05-ltree-hierarchy.sql`.

Seeded one table carrying both an adjacency-list `parent_id` and a maintained `ltree` path column,
shaped to match the "combined production tree" performance-budget scale
(`docs/traceability/performance-budgets.md` §1: branching `[10,5,6,7,7]` then 12 leaves per
depth-5 branch, plus a 9-level single-child chain off one leaf reaching depth 15) — 193,569 nodes,
close in order of magnitude and shape to the ~193,500-row scale the real performance tests use.
Every query family the remediation plan names as an `ltree` candidate was run twice — once as the
recursive-CTE form the real schema uses today (schema version 0013's `job_node_ancestors`/
`job_node_descendants`, and 0015's `resolve_rate` chain-walk pattern), once as the `ltree`-operator
equivalent — both under `EXPLAIN (ANALYZE, BUFFERS)`, with the target node's id/path materialized
once via `psql`'s `\gset` so neither form pays for locating the target more than once (a fairness
fix applied after an initial run where a repeated "find the deepest node" subquery was inflating
both sides' numbers roughly equally, so relative comparisons still held even before the fix).

| Query family | Adjacency-list (recursive CTE) | `ltree` (GiST-indexed operator) | Verdict |
|---|---|---|---|
| Ancestor walk (depth-15 node, 14 ancestors) | 0.034 ms | 0.042 ms | No meaningful difference — both trivial at this ancestor count |
| Descendant/subtree walk (~19,364-node branch) | 12.476 ms | 2.345 ms | `ltree` ~5.3x faster |
| Nearest-ancestor lookup (4 scattered overrides) | 0.027 ms | 0.011 ms | No meaningful difference — both trivial at this data volume |

**Result: the broad descendant/subtree family is the only query shape where `ltree` shows a real,
reproducible win.** Ancestor walks and nearest-ancestor lookups (the `resolve_rate`/
`user_rate_boundaries` shape) are already fast enough with the adjacency-list recursive CTE that the
`ltree` form's GiST-index lookup overhead roughly cancels out its shorter walk. Critically, the
current recursive-CTE descendant query (12.5 ms at this scale) already clears its documented budget
(`docs/traceability/performance-budgets.md` §2: 100 ms for recursively-derived-achievement queries at
combined-production-tree scale, the same query shape `node_succeeded` uses internally) with an 8x
margin — there is no budget currently being missed that `ltree` would need to rescue.

Against the exit rule's second clause: adopting `ltree` would require `move_job_node` (schema version
0016) to rewrite the path column for the moved node **and every descendant** on every move — an
O(subtree size) write replacing the current O(1) `parent_id` update — and that rewrite would need its
own concurrency proof that it cannot introduce a write-skew window between two concurrent moves of
overlapping subtrees, on top of the three hazards already proven in this report (spikes 2 and 4, and
the cross-domain move-vs-prerequisite-edge race covered by
`Concurrent_move_and_prerequisite_edge_that_would_jointly_violate_ancestor_descendant_exclusion_allow_exactly_one_to_succeed`
in `JobPrerequisiteSchemaContractTestsBase`). That is exactly the "materially complicate the
concurrency proof" the exit rule warns against, for a benefit that does not close any currently-open
budget gap.

**Conclusion: `ltree` is explicitly rejected.** No `ltree` extension, path column, or index is added
to the production schema. If a future descendant/subtree query is added whose budget the current
recursive-CTE approach cannot meet at accepted scale, this spike's numbers are the baseline to beat
and the O(subtree)-write/concurrency-proof cost above is the price of clearing that bar.

## Summary

| # | Area | Result | Consequence |
|---|---|---|---|
| 1 | Single-root invariant | PASS (+ counterfactual confirms design) | ADR 0015 unchanged; counterfactual is documentation evidence |
| 2 | Prerequisite-cycle detection | PASS (locked); FAIL (unlocked, as expected) | ADR 0012 updated: "prerequisite-graph writes" is now a proven, not merely anticipated, lock domain |
| 3 | GiST session-overlap exclusion | PASS | New requirement for §7.4 error translation: map deadlock (`40P01`) to the same category as `23P01` for this race |
| 4 | Advisory-lock ordering | PASS | ADR 0012's ordering rule confirmed sufficient |
| 5 | DST gap/fold resolution | PASS | ADR 0008's resolver composition confirmed correct against TZDB 2026b |
| 6 | Concurrent cost sweep vs. oracle | PASS | ADR 0009's exact-rational allocation approach confirmed sound at N up to 120 |
| 7 | `ltree` vs. adjacency-list hierarchy queries | REJECTED | Remediation plan §3.2: no schema change; recursive-CTE descendant queries already clear budget, and `ltree` adoption would add an O(subtree)-write concurrency-proof burden for no open budget gap |

All six spikes required by plan §5.3 are complete. §5.5's foundation exit criterion "the de-risking
spikes demonstrate the required PostgreSQL behaviour under concurrent writes, and the time model
handles the documented DST cases deterministically" is satisfied. Spike 7 was added later, for the
PostgreSQL column-type remediation plan's own §3.2 exit rule, and reached an explicit
reject-with-evidence outcome rather than a pass/fail against a pre-existing invariant.
