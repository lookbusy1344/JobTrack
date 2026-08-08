# JobTrack performance and scale budgets

**Closes:** Implementation plan §5.4, §5.5 exit criterion ("performance and scale budgets are defined and recorded").

Defined now, before schema design, so the database gate (§6.7) tests against agreed targets instead
of retrofitting them after the fact. Budgets are for PostgreSQL, the production-authoritative
provider (plan §2); SQLite's documented single-writer envelope (§6.4) is exempt from the latency
figures below but must still complete each operation without unbounded blocking, per its own
functional (not performance) budget, noted per row.

Every budget here is a **target**, not a design constraint baked into the schema by fiat: §6.7
tests assert plan shape and latency against these numbers on the representative scales below,
without brittle exact-cost assertions (plan §6.5). A budget proven wrong by measurement is revised
here, with the reason recorded, not silently loosened at the test.

## 1. Representative dataset scales

| Scale name | Definition | Purpose |
|---|---|---|
| **Deep tree** | One hierarchy chain 50 levels deep, single child per level from level 10 downward (a plausible worst case for ancestor-chain rate/prerequisite resolution, ADR 0009's node-override boundary set) | Ancestor-chain traversal, nearest-ancestor rate resolution, readiness explanation |
| **Broad tree** | One branch with 10,000 direct leaf-work children | Sibling listing, subtree aggregation, hierarchy pagination |
| **Combined production tree** | 200,000 `job_node` rows total, mixed depth (median depth 6, max depth 15), mixed breadth | Whole-tree operations, schema-introspection baseline, general query-plan review |
| **Long history** | One `job_node` subtree with 5 years of daily `work_session` rows for 20 users (≈ 36,500 sessions), plus 5 years of daily schedule exceptions | Historical schedule/rate resolution, cost recalculation over a long `asOf` range, historical-correction re-validation (ADR 0003) |
| **Many users** | 2,000 `app_user` rows, each with an effective-dated rate timeline of 10 changes over 5 years | User-rate boundary resolution, rate-timeline lookups at scale |
| **High concurrency** | One worker with 100 concurrent open `work_session` rows across 100 different leaves at the same instant (the `N = 100+` golden scenario, GS-010) | Database-wide overlap discovery (§10.2.2), concurrency-divisor computation, elevated-scope cost read (ADR 0017) |
| **Overlapping-cost scale** | 50 workers x 400 leaves each (20,000 `job_node` total), a per-worker 6-deep sliding-window `work_session` staircase (deterministic, closed-form concurrency depth), 24x7 weekly schedules, a 3-edge per-worker `user_cost_rate` timeline crossing the staircase window, ~1 forward prerequisite edge per adjacent leaf pair; plus an optional 51st "heavy" worker with 5,000 sessions in the same shape. Full algorithm and rationale: `docs/plans/2026-07-09-overlapping-cost-scale-plan.md` §4/§5 | Cost calculation and concurrency-divisor computation at a realistic per-worker session count (impl plan §7.2's cost engine); the heavy worker separately bounds the segment partitioner's O(P^2) tail |

Generators for these scales live in `JobTrack.TestSupport` once implementation starts (plan §6.6);
this table is the specification they are built against, including a recorded seed per generated
scale so a failing scale run reproduces exactly (plan §6.6 "preserve failing seeds as regression
fixtures").

## 2. Latency and query-plan budgets

Measured at the **combined production tree** scale unless a row names a different scale. "P95
latency" is wall-clock for the canonical query (§6.5) end to end, including EF materialization,
against a warmed connection pool, single concurrent caller unless the row says otherwise.

| Operation | Scale | P95 latency budget | Query-plan requirement |
|---|---|---|---|
| Subtree/ancestry traversal (single node, full ancestor or descendant set) | Deep tree | 50 ms | Index-only or index scan on the hierarchy closure structure; no full-table scan |
| Broad-branch child listing (paginated, 50 rows/page) | Broad tree | 200 ms (revised from 30 ms — see note below) | Index scan on parent-id, no sort spill to disk |
| Recursively derived achievement for one branch | Combined production tree | 100 ms | Recursive CTE (§6.5) bounded by branch size, not whole-tree size |
| Unsatisfied-prerequisite explanation for one leaf | Combined production tree | 100 ms | Recursive CTE terminates at first satisfied ancestor per path; no whole-graph materialization |
| Database-wide overlap discovery for one worker, at one instant | High concurrency | 75 ms | User-leading GiST/B-tree index scan (§6.3); no sequential scan of `work_session` |
| Cost calculation for one leaf, single `asOf` | Long history | 800 ms (revised 2026-08-06 from the pre-implementation 150 ms target — measured ~586-658 ms, dominated by first-call process warm-up, not the read; see the long-history section below) | Canonical cost-input query (§6.5) plan uses the temporal indexes on `work_session`, schedule, and rate ranges; no nested-loop over the full history |
| Cost calculation for one branch (100 leaves), single `asOf` | Long history × Broad tree | 500 ms (revised 2026-08-06 from the pre-implementation 2 s target — measured ~344-356 ms; see the long-history section below) | Batched cost-input materialization, not N+1 per-leaf queries |
| Cost calculation for one leaf, single `asOf` | Overlapping-cost scale | 150 ms (measured 87.5 ms, then 95.8 ms after the GiST-index fix below re-measured against a warm, ANALYZEd table) | Cost-input session load goes through `worker_overlapping_sessions` (schema version 0018); for a query spanning most/all of a worker's history, a plain index scan on `worked_by_user_id` correctly beats GiST (no pruning to do at high selectivity) and no sequential scan occurs |
| Cost calculation for one branch (400 leaves, single worker), single `asOf` | Overlapping-cost scale | 2 s (measured 72.1 ms, then 52.6 ms after the fix below) | Same cost-input query, batched per-worker materialization, not N+1 per-leaf queries |
| Effective-dated rate/schedule lookup for one user at one instant | Many users | 20 ms | Range-index lookup (GiST or B-tree per §6.3), not a scan of the user's full timeline |
| Bulk cost enrichment for one listing page (200 candidates, the HTTP API's `MaxPageSize`) | 200-leaf single-branch, single-worker fixture (`CostQueryPortContractTestsBase.GetBulkNodeCostsAsync_prices_a_maximum_width_page_of_candidates_promptly` and `.GetBulkNodeCostsAsync_keeps_commands_and_connections_constant_at_maximum_width`) | 10 s; at most 16 database commands; at most 1 concurrently open connection (the command count must also equal the one-candidate baseline) | `ICostQueryPort.GetBulkCostInputsAsync` materializes one snapshot regardless of candidate count; PostgreSQL invokes `worker_overlapping_sessions` once through a set-based lateral query across every contributing worker, never once per row or worker |
| Schema deployment, empty database | — | 30 s | N/A (one-time operation; budget guards against an accidentally slow migration script) |
| Schema deployment, upgrade from oldest supported version | Combined production tree | 5 min | N/A; recorded per ADR 0011's "any prior version" upgrade window — a script exceeding this budget on production-scale data is reviewed before merge, not after |
| Awaiting Progress page load (`GetAwaitingProgressInputsAsync`) | Broad tree / Combined production tree | No formal budget yet — see curve below | Loads unfinished leaves, their ancestor chains, and required-job achievement facts; regression suite asserts both latency and materialized-node bounds |
| Single-leaf cost read, zero sessions (`GetCostInputsAsync`) | Broad tree / Combined production tree | 150 ms (broad tree) / 400 ms (combined production tree), regression ceilings | Loads only the requested subtree plus needed ancestor chains (`CostQueryAssembly.LoadSubtreeAsync`/`ExtendAncestryAsync`) — narrowed 2026-07-24, see below |

**Full-table hierarchy load curve (2026-07-24, code-review-scalability-remediation-plan §2.2).**
Originally, `PostgreSqlAwaitingProgressQueryPort` and `PostgreSqlCostQueryPort`'s node load both
materialized the entire `job_node` (+ `leaf_work` + prerequisite) table before doing any
request-specific work — the cost read's `maxHierarchyNodes` cap only bounded the *requested
subtree*, not this upfront load. Measured end to end through the ports (EF materialization included,
warmed connection pool, single caller), zero work sessions so the cost figure isolates the node-load
cost from ADR 0017's separate per-worker session load:

| Scale | `job_node` rows | Awaiting Progress (before) | Awaiting Progress (after §2.2 step 4) | Single-leaf cost read (before) | Single-leaf cost read (after §2.2 step 2) |
|---|---|---|---|---|---|
| Broad tree | 10,002 | 31.5 ms | 30.1 ms (10,002 nodes loaded — every leaf still `Waiting`) | 21.4 ms (10,002 nodes loaded) | 7.1 ms (3 nodes loaded) |
| Combined production tree, every leaf `Waiting` | 193,570 | 783.0–806.0 ms | 744–1,285 ms (193,570 nodes loaded — every leaf legitimately unfinished, so the narrowing has nothing to exclude) | 360.2 ms (193,570 nodes loaded) | 100.9 ms (7 nodes loaded) |
| Combined production tree, realistic ~98% completion ratio | 193,570 | *(not measured — no fixture existed for this scale before step 4)* | 572 ms (3,887 nodes loaded) | — | — |

Both operations originally scaled linearly with *total* table size (≈4.1 µs/node for Awaiting
Progress, ≈1.85 µs/node for the cost read) — every additional installation-wide `job_node` row cost
every view the same fixed amount, regardless of whose data it was. At the combined production tree
scale — this project's own standing definition of a plausible production install size (§1) — a
*single-leaf, zero-session* cost read cost 360 ms purely from this upfront load, before the operation
had done anything the request actually asked for.

**Step 2 (cost read) is fixed.** `CostQueryAssembly.LoadSubtreeAsync` now loads only the requested
root(s)' own subtree, through a new set-based recursive query (PostgreSQL: `job_node_subtrees`/
`job_node_ancestor_chains` stored functions, schema version 0013; SQLite: a parameterized recursive
CTE mirroring `SqliteControlledLeafQuery`'s established pattern). `ExtendAncestryAsync` then extends
the loaded node/owner maps with exactly the ancestor chains ADR 0017's elevated read scope still
needs: each requested root's own path to the true root (a rate override can be declared above the
requested subtree; ADR 0040's owner carve-out walk needs it too), and, for any contributing worker's
session on a leaf outside the requested subtree, that leaf's own path to the root (`RateResolver`'s
nearest-ancestor-override walk). The node count actually loaded dropped from the whole table
(193,570) to 7 for a single leaf at combined-production-tree scale — a >27,000x reduction — and
latency from 360 ms to ~101 ms (the residual is largely fixed per-request overhead: connection open,
transaction snapshot, `GetActorRolesAsync`, ANALYZE-less-table planning — not proportional to
installation size any more). `CostQueryPortContractTestsBase.
GetCostInputsAsync_excludes_nodes_outside_the_requested_subtree_while_still_resolving_a_true_root_override`
proves both the narrowing (a 30-node decoy subtree never appears in `NodesById`) and correctness (a
rate override on the true root, above the requested leaf's own subtree, still resolves) on both
providers. `FullTableHierarchyLoadPerformanceTests` now asserts a 150 ms / 400 ms regression ceiling
for the cost read (broad tree / combined production tree) — tight, not generous, since the operation
is no longer installation-size-dependent.

**Step 4 (Awaiting Progress) is fixed 2026-07-25.** `PostgreSqlAwaitingProgressQueryPort`/
`SqliteAwaitingProgressQueryPort` no longer load the whole `job_node`/`leaf_work`/`job_prerequisite`
tables. The earlier premise — that a fix here needed "a different, larger mechanism" because
Awaiting Progress's roll-up "genuinely needs every node" — turned out to be false once investigated:
`AwaitingProgressCalculator` never aggregates branch achievement itself; only the readiness check for
a *blocking prerequisite's required job* ever needs recursive achievement, and that can be resolved
one required job at a time through the same already-correct `node_succeeded` (PostgreSQL) /
`JobNodeHierarchyQueries.IsSubtreeAchievedSqliteAsync` (SQLite) mechanisms the single-node readiness
and Browse achievement checks already use — no new stored function, no maintained aggregate.

The narrowed load is: every currently-unfinished leaf (childless, not archived, no `leaf_work` or a
non-terminal achievement) via plain EF LINQ; each candidate's own ancestor chain to the true root
(reusing `job_node_ancestor_chains` on PostgreSQL, a parameterized recursive CTE on SQLite — the same
elevated-scope shape as step 2's cost-read narrowing); the prerequisite edges reachable from that
scope; and, only for a required job *outside* that scope, its achievement resolved through the
mechanisms above rather than materializing its subtree.

The table above shows why this needed a *third* measurement scale, not just the existing two: the
"combined production tree" fixture seeds every leaf `Waiting`, so every leaf legitimately belongs on
Awaiting Progress's list regardless of whether the load itself is narrowed — that scale mainly proves
no regression (744–1,285 ms observed, within the 1.5 s ceiling in place at the time — widened to
2.5 s 2026-07-27 after a full-suite run measured 1,512.9 ms against it, the same shared-PostgreSQL
contention documented elsewhere in this section, not a query regression; the narrowing even costs a
little there, since the query now runs a correlated "has no children" check per row instead of a
plain sequential scan, and there is nothing to exclude when literally every leaf is a candidate). The
new realistic-completion-ratio scale (~98% of leaves finished, a mature installation's typical
shape) is where the narrowing actually pays off: 3,887 of 193,570 nodes loaded, 572 ms. This remains
an O(total `job_node` rows) database-side scan (no index yet accelerates "find every childless,
unfinished leaf"), so the improvement here is the avoided full in-memory graph construction, not a
sub-linear query — a future covering index is the natural next step if this ever needs tightening
further, but is not needed to close this finding. A new dual-provider contract test,
`AwaitingProgressQueryPortContractTestsBase
.Excludes_a_large_unrelated_finished_subtree_while_a_cross_branch_prerequisite_still_resolves_correctly`,
proves both the narrowing (a 30-node finished decoy subtree never appears in `NodesById`) and
correctness (a cross-branch required job that is itself finished, and so is not an unfinished-leaf
candidate, still resolves through the override path). `FullTableHierarchyLoadPerformanceTests` keeps
its 500 ms / 1.5 s ceilings for the two pre-existing scales (the latter widened to 2.5 s 2026-07-27,
see above) and adds an 800 ms ceiling for the new realistic scale.

**2026-07-25 scalability-follow-up plan (§2.1-§2.7): request-scoped Awaiting Progress, cost
authorization, and cost history reads.** All figures below are warm (§2.7's protocol: a throwaway
port call pays the one-time EF query-compilation/connection-establishment cost before any stopwatch
starts) unless marked cold; none of this section's rows carry a separate cold-start budget.

- **§2.1 (request scoping).** `GetAwaitingProgressInputsAsync` now takes an `AwaitingProgressQueryFilter`
  (ownership, optional subtree root, search text, offset/limit) and applies it — plus the exact
  descending-priority/ascending-deadline-nulls-last/ascending-id ordering `AwaitingProgressCalculator`
  used to apply in memory — in the port's own query, before paging. A subtree root's recursive
  relation is composed into that same candidate query; the subtree's IDs are not first materialized
  into the application process. The permanent installation root short-circuits that recursion (a
  production-scale regression measured ~762 ms for needless true-root traversal versus ~84 ms
  unscoped before the short-circuit, and now guards both shapes with the existing 500 ms ceiling --
  widened from 300 ms after a full-suite `dotnet test JobTrack.slnx` run measured 317 ms, the same
  contention already documented for the search row below).
  No new latency row: existing
  ceilings already cover the unfiltered/unbounded shape (kept as a deliberate worst-case regression
  guard, see below) and the new default-page shape (next row).
- **§2.2 (candidate-discovery index decision).** `EXPLAIN (ANALYZE, BUFFERS)` for the production-
  realistic shape (no ownership/subtree/search filter, one default page) against the realistic ~98%-
  finished combined-production-tree fixture: ~34 ms, dominated by the childless-check anti-join
  against the existing `job_node_parent_id_idx` (17,211 index probes), not a sequential-scan
  bottleneck. No partial index is evidence-backed at this scale. Regression-guarded at 1,500 ms
  (`FullTableHierarchyLoadPerformanceTests.Awaiting_progress_with_a_realistic_default_page_at_combined_production_tree_scale_stays_within_ceiling`)
  -- originally 500 ms, widened to that from a 317 ms contended measurement (§2.1 note below), then
  widened again 2026-07-27 after a further full-suite `dotnet test JobTrack.slnx` run measured
  911.6 ms against the 500 ms ceiling: the surrounding suite has grown since that first revision, so
  the shared-instance contention window is wider than it was. Same precedent as §2.1/§2.3/the
  broad-branch child listing row below -- the query is not slower, headroom is added above the newly
  observed contended figure.

  **Re-measured 2026-07-25** (fresh-eyes efficiency review) to check two things the original entry
  left open — whether the ordering itself needs an index now that §2.1 moved sorting and paging into
  SQL, and whether the partial index §2.2's target design hypothesised would in fact be used. Same
  fixture, 193,570 `job_node` rows, 3,529 unfinished candidates. Both answers are negative, and the
  original conclusion stands:

  - **The sort is not the bottleneck.** The plan sorts 17,211 rows (`quicksort`, ~1.1 MB per worker)
    before `LIMIT 51`, because the childless anti-join is applied *after* the sort as the outer side
    of a `Nested Loop Anti Join`. The sort adds roughly 2 ms on top of its own input scan. A
    composite `(priority_id DESC, …)` index could not serve it regardless: two of the four sort keys
    are `COALESCE(needed_finish, needed_start)` expressions, and the candidate predicate needs the
    `leaf_work` join resolved before any ordering can be read off an index.
  - **A partial `leaf_work (job_node_id) WHERE achievement_id IN (1,2)` index is provably ignored.**
    Created it, re-`ANALYZE`d, re-planned: byte-identical plan, same 51,582 buffer hits, still a
    `Parallel Seq Scan on leaf_work`. It cannot be used, because the candidate predicate's
    `lw.achievement_id IS NULL OR lw.achievement_id IN (1,2)` needs every `leaf_work` row to resolve
    the `IS NULL` branch — a partial index over the non-terminal rows alone cannot answer it. Dropped
    again; no schema change.

  **The one real sensitivity is the visibility map, not an index.** On the freshly-seeded fixture the
  anti-join's `Index Only Scan using job_node_parent_id_idx` reported `Heap Fetches: 17160` and the
  query ran at ~64 ms. After `VACUUM (ANALYZE) job_node, leaf_work` the same plan reported
  `Heap Fetches: 0` and ~34.6 ms — reproducing the ~34 ms figure recorded above, which confirms that
  measurement was taken against vacuumed tables. So the recorded budget carries an operational
  dependency worth stating explicitly: this query is index-only-scan-bound, and its ~34 ms figure
  assumes autovacuum keeps `job_node`'s visibility map current. An installation where autovacuum is
  starved or disabled should expect roughly the 64 ms shape instead — an operations concern, not a
  schema or query defect.
- **§2.3 (search-index decision).** A zero-match `LOWER(description) LIKE '%term%'` search (the
  worst case — PostgreSQL cannot stop early the way a selective match would let it) against the
  plain combined-production-tree fixture (~193,500 rows, no narrowing filter applies to a whole-tree
  search): a parallel sequential scan completes in ~20 ms. Not material at this scale, so neither an
  index (pg_trgm/GIN) nor the cross-provider tokenizer/prefix-semantics ADR the plan's target design
  calls for if one were needed is currently justified. Regression-guarded at 700 ms
  (`FullTableHierarchyLoadPerformanceTests.Search_with_no_matches_at_combined_production_tree_scale_stays_within_ceiling`);
  after the required full-suite run exposed shared-PostgreSQL contention
  (~450 ms contended versus ~20 ms isolated), as recorded below.
- **§2.4 (cost authorization).** A single-node cost read previously fetched actor roles and ancestor
  owners via two separate round trips, then `GetCostInputsAsync` resolved roles a third time
  internally (a copy never read on this path). `ICostQueryPort.GetCostAccessInputsAsync` now returns
  both from one repeatable-read transaction; `GetCostInputsAsync` takes no actor id and does not
  resolve roles. The transaction boundary matters: without it, the two statements could observe
  different role/ownership states under a concurrent update even though they shared one port call.
  Command-count contract tests prove the single-node read stays bounded (≤16 commands, matching the
  bulk path's own ceiling) and that a denied actor's read issues strictly fewer commands than an
  authorized one (never opens the worker-materialization connection).
- **§2.5 (schedule/override history).** Cost assembly previously loaded every schedule version a
  contributing worker has ever had (no time filter) and every node-rate override that worker has on
  any node anywhere (time-bounded, but not node-scoped). Schedule versions are now prefiltered by a
  one-day-widened UTC window around the cost bounds (safe regardless of a version's own IANA zone,
  since any zone's offset is under 24h and `ScheduleExpander` clips exactly per-zone downstream
  regardless). Node-rate overrides are now loaded after `ExtendAncestryAsync` determines the final
  node set, filtered by that set — `RateResolver` only ever walks a session's own node and its
  ancestors, so an override elsewhere can never be consulted. `UserCostRate` stays worker-wide/time-
  only (it carries no `NodeId`). Contract tests prove a decade of superseded schedule versions and
  unrelated node overrides on decoy nodes change neither the calculated total nor
  `WorkerCostInputs.NodeOverrides`.
- **§2.6 (partitioner output cardinality).** The heaviest realistic fixture this codebase seeds (one
  worker, 5,000 sessions, 6-deep staircase) produces 30,000 cost-trace segments — 60% of
  `CostQueries.MaxCostTraceSegments`'s hard allocation cap (50,000). `PartitionBounded` counts every
  active session in the concurrency divisor but emits only requested-subtree allocations, and throws
  before materializing more than the remaining trace budget across workers; hierarchy-only reads skip
  trace construction altogether. The cap therefore bounds computation output and memory rather than
  rejecting only after an oversized trace has already been built. No bounded aggregate trace
  representation is introduced yet (the plan's own trigger is exceeding the response limit, not
  approaching it). Regression-guarded at 35,000 segments
  (`OverlappingCostScalePerformanceTests.Heavy_worker_with_5000_sessions_bounds_the_partitioners_quadratic_tail`).
- **§2.7 (benchmark protocol).** `FullTableHierarchyLoadPerformanceTests` now explicitly warms its
  pooled `NpgsqlDataSource` before timing in every test (a throwaway port call before the stopwatch
  starts), rather than relying on an earlier test in the same run having already paid that cost —
  this project's `xunit.runner.json` sets `stopOnFail`, so a test can otherwise legitimately run
  alone. Isolating a test that lacked this discipline previously showed ~550-570 ms cold (first-ever
  query of that shape in the process) versus ~34-120 ms warm for the identical query — the gap is
  JIT/EF-compilation/connection-establishment cost, not the query itself.

**Per-request security-stamp validation tax:** `Program.cs` sets `SecurityStampValidatorOptions.
ValidationInterval = TimeSpan.Zero`, so every authenticated request re-validates the security stamp
against the identity store — one DB round-trip before the request's own reads, on every page view
and API call (spec §7.1's instant-revocation requirement motivates it; this is not a defect). It
compounds whichever of the above rows a given request also hits and is not itself included in any
measured number in this document. If the full-table-load curve above ever needs remediation, revisit
this fixed tax at the same time rather than separately — a short validation interval (5–15 s) is the
documented relaxation option (code-review-scalability-remediation-plan.md §2.3), not a change to make
unilaterally without an ADR.

**Rows not yet tested (§6.7 database-phase performance-test work):** the cost engine (plan §7.2) has
now landed (M6 library gate, ADR 0026; M8 web gate, ADR 0027), so the two "cost calculation" rows
against the **overlapping-cost scale** are now measured, per
`docs/plans/2026-07-09-overlapping-cost-scale-plan.md`. The two rows against the **long history**
scale are now measured too (below) — see that section for the finding it produced. The "upgrade from
oldest supported version" schema-deployment row is still deferred: constructing it faithfully means
deploying only the earliest schema versions, seeding combined-production-tree scale, then applying
every remaining version — disproportionate scaffolding for one budget row at this stage. All other
rows in this table, plus every row in §3, are covered by `JobTrack.Database.PerformanceTests`.

**Long-history scale measurements (2026-08-06, `2026-08-06-cost-read-materialisation-reduction-plan.md`
Stage 1):** `PerformanceScaleGenerator.SeedLongHistoryScaleAsync` builds this scale as specified
above — one subtree, 20 workers, 5 years of daily `work_session` rows (36,500 sessions) and daily
`user_schedule_exception` rows (an unpriced daily lunch-break `RemoveWorkingTime` exception per
worker), each worker on a 24x7 weekly schedule spanning the whole window. Measured end to end
through `CostQueries` (`LongHistoryScalePerformanceTests`), single concurrent caller, warmed
connection pool:

| Operation | Before (Stage 1) | After Stages 2-4 | After the engine follow-up (below) | Revised budget |
|---|---|---|---|---|
| Cost calculation for one leaf, single `asOf` | **1,217.6 ms** | **~608-670 ms** | **~586-658 ms** | 800 ms |
| Cost calculation for the 20-worker branch, single `asOf` | **8,174.6 ms** | **~721-745 ms** | **~344-356 ms** | 500 ms |

Both originally far exceeded the pre-implementation 150 ms/2 s targets — a genuine,
previously-unmeasured defect, not fixture noise. The plan's own §2.1 originally hypothesised the
cause as `ScheduleExpander` materialising years of calendar days regardless of session sparsity; the
measurement disproved that for this scale (instrumentation recorded 36,520 scheduled working
intervals against 36,500 sessions — a 1.00 ratio, so expansion produced almost exactly one interval
per session, nothing wasteful to trim, because this scale's sessions are dense — one per worker per
day — leaving no calendar gap). A follow-up in-process microbenchmark isolated the real cost to
`IntervalAlgebra.Subtract`: resolving one worker's 1,825 scheduled intervals against 1,825 disjoint
schedule exceptions took **378.5 ms**, and the same call repeated for 20 workers took
**4,615.4 ms** — the large majority of the port's own 7,690.5 ms DB-and-CPU materialisation time.
`Subtract` iterated the *full* cut list for every minuend interval (O(M×C)) even though both lists
are already sorted and disjoint by the time it runs — exactly the shape `IntervalIndex`
(2026-07-25) already searches in better-than-linear time, which `Subtract` never adopted.

**Fixed 2026-08-06 (Stage 4).** `Subtract` now builds one `IntervalIndex` over the normalized cuts
and aggregates each minuend interval only over its actually-overlapping cuts. The materialisation
stage this plan targets — `GetCostInputsAsync`'s DB-and-CPU input assembly — dropped from
~7,690 ms to **~245-260 ms, a ~30x reduction**, exactly as the microbenchmark predicted. The
overlapping-cost scale's heavy-worker case (5,000 sessions) improved too, 1,141.1 ms → 701.4 ms,
confirming the fix pays off generally, not only at this fixture.

**Fixed further 2026-08-06 (Stage 5, evidence-gated column projection).** A wide-vs-narrow raw read
of `user_schedule_exception` at this scale (36,500 rows, all 9 columns vs the 5
`CostQueryAssembly` reads) measured 50.4 ms vs 9.7 ms — entity-shaping cost visible next to the
query itself, satisfying the plan's own evidence gate (every other worker-scoped load in the same
method stays in the tens of rows, not worth projecting). The exceptions load now projects to only
the columns read. Materialisation dropped further, **~253 ms → ~184 ms (~28% additional
reduction)**, closely matching the estimate. Combined Stages 2-5 reduction: **~7,690 ms → ~184 ms,
a ~42x total win**, closing every finding in this plan's §1/§2 scope.

**New finding surfaced by the same measurement, fixed the same day:
`CostSegmentPartitioner`/`CostEngine` computation cost.** Fixing the materialisation stage unmasked
a second, larger remaining term — the pure engine's own partition-and-calculate pass over this
scale's ~36,500 sessions, measured at ~359-508 ms across 20 workers. An engine-level reproduction
(`CostEngineTests.Costing_twenty_workers_of_five_year_daily_sessions_against_daily_exceptions_stays_fast`,
failing at 486 ms against a 200 ms ceiling) isolated the cause: `RateResolver.Resolve` runs once per
segment allocation, and every resolution began with a linear scan of the worker's *full*
schedule-exception list — O(allocations × exceptions), the same quadratic shape `Subtract` had, and
at this scale a scan of 1,825 entries per resolution in which every entry is an unpriced
`RemoveWorkingTime` exception that can never resolve a rate. **Fixed 2026-08-06:**
`RateResolver.FilterPricedExceptions` filters the set once per calculation to the priced additive
entries that can ever match (hoisted by the engine exactly as `IndexOverridesByNode` already was),
and a second, benchmark-guided pass cut the hot loops' allocation churn and improved locality —
flat sorted arrays instead of per-instant dictionaries and tree sets in
`CostSegmentPartitioner`, single-pass grouping and an in-place sort instead of LINQ chains in
`CostEngine.Calculate`, cursor-based gap emission instead of nested enumerator chains in
`IntervalAlgebra.Subtract`/`Normalize`, and a no-clone fast path for ADR 0017's trace narrowing when
nothing narrows (in-process, the 20-worker engine pass measured partition 123→74 ms, calculate
92→72 ms, subtract 41→22 ms, with allocated bytes roughly halved). End to end: branch
**~721-745 ms → ~344-351 ms**; the leaf figure (~586-658 ms) is dominated by first-call process
warm-up (EF model build, pool spin-up — it runs first in the test process), not by the read itself.
Per §4 below, the revised budget column above reflects measured capability with ~1.35-1.4x headroom
over the highest observed run — not the original pre-implementation target, and not silently
dropped either.

**Overlapping-cost scale measurements (2026-07-09, plan §6/§7):** measured end to end through
`CostQueries` (EF materialization included), single concurrent caller, warmed connection pool. Leaf:
87.5 ms against the 150 ms budget. Branch (the single worker's own 400-leaf branch — deliberately
harder than the original row's "100 leaves across potentially many workers", since plan §2.4's own
finding is that *sessions-per-worker*, not leaf count, dominates cost latency): 72.1 ms against the
2 s budget. Leaf and branch latency come out close (87.5 ms vs 72.1 ms) not by coincidence but by
construction: `GetCostInputsAsync` loads a contributing worker's *entire* database-wide session
history regardless of whether the request names one leaf or the whole branch (ADR 0017's elevated
read scope for a correct concurrency divisor), so both queries pay the same worker-scoped
materialization cost — the requested node only changes how many nodes `CostEngine` aggregates
output for, not how much is read. DB-materialization-vs-pure-engine split for the branch query:
38.6 ms DB, 14.6 ms pure engine (`CostSegmentPartitioner` + `CostEngine`) — the engine is not the
bottleneck at this scale. The optional heavy worker (5,000 sessions, same staircase shape, no budget
assigned per plan §7): 1,141.1 ms, a ~13x latency increase against a ~12.5x session-count increase —
consistent with the segment partitioner's O(P²) term starting to dominate, though still well short of
threatening the 400-session-worker budget above. Worth re-measuring if realistic per-worker session
counts ever approach that range.

**GiST-index fix (schema version 0018):** the whole-history queries above never actually exercised
`work_session_user_range_gist_idx` — every query window in this scale spans a worker's *entire*
history, so there is no non-matching tail for a range index to prune, and a plain
`worked_by_user_id` index scan is genuinely (and correctly) the cheaper plan. A **narrow** query
against a worker's long history is a different story: `CostQueryAssembly.LoadWorkersAsync` was
duplicating `worker_overlapping_sessions`'s predicate as plain LINQ
(`StartedAt < end && (FinishedAt == null || FinishedAt > start)`), which is never sargable against
any range index regardless of which is present — Postgres fell back to filtering a worker's *entire*
history in memory. Fixed by calling through the stored function (now rewritten to test
`session_range && tstzrange(...)`, sargable against the GiST index) instead of duplicating its
predicate, plus running `ANALYZE` after seeding (a bulk-loaded table has no fresh statistics yet;
production's autovacuum keeps them current as sessions accumulate gradually, so this was a fixture
artifact, not a production gap). Measured on a 5,000-session/~208-day worker queried with a narrow
window: 0.31 ms → 0.009 ms, 89 → 4 block reads. Regression-tested (`OverlappingCostScalePerformanceTests`)
by querying a late leaf under that worker, which naturally produces a narrow window against its long
prior history, asserting `work_session_user_range_gist_idx` is used.

**Prerequisite fan-out (2026-07-28 fresh-eyes review §2.2):** `job_node_blocked()`'s original shape
called `node_succeeded` once per `job_prerequisite` edge into an unsatisfied required job, rather than
once per distinct required job — a required branch shared by many dependents repeated the same
recursive traversal once per dependent. New fixture (`PerformanceScaleGenerator.SeedPrerequisiteFanOutAsync`):
one required branch with a small unfinished subtree (never succeeds), 5,000 dependent leaves each
declaring their own direct prerequisite on it, a realistic 1-in-3 finished/unfinished mix among the
dependents themselves (`FullTableHierarchyLoadPerformanceTests
.Job_node_blocked_at_prerequisite_fan_out_scale_resolves_the_required_branch_once` /
`.Awaiting_progress_at_prerequisite_fan_out_scale_resolves_the_required_branch_once_per_distinct_job`).

`EXPLAIN (ANALYZE, BUFFERS)` of `SELECT id FROM job_node_blocked();` alone against this fixture:

- **Before** (original per-edge shape): `Seq Scan on job_prerequisite  Filter: (NOT node_succeeded(from_id))`
  ahead of the `DISTINCT`'s `HashAggregate` — `node_succeeded` evaluated once per edge (5,000 times).
  Execution time 46.7 ms.
- **First rewrite attempt** (plain `WITH RECURSIVE required(id) AS (SELECT DISTINCT from_id ...),
  unsatisfied(id) AS (SELECT id FROM required WHERE NOT node_succeeded(id))`, no `MATERIALIZED`):
  identical plan and identical per-edge evaluation. PostgreSQL 12+'s planner is free to inline a
  non-recursive CTE and push the filter back down ahead of the aggregate it was meant to run after —
  the rewrite alone changed nothing measurable.
- **After** (both `required` and `unsatisfied` pinned `MATERIALIZED`): `CTE Scan on required ...
  Filter: (NOT node_succeeded(id))` runs once (`rows=1 loops=1`), then the recursive `blocked` term
  joins every one of the 5,000 edges against that single cached result. Execution time 3.9 ms — a
  ~12x improvement, and now genuinely independent of dependent fan-out rather than merely
  coincidentally fast at this scale. `Awaiting_progress_...` shows the same effect one layer up:
  `ExcludeBlocked=true` (which composes `job_node_blocked` as an `EXISTS`) dropped from 97.7 ms to
  11.4 ms; `ExcludeBlocked=false` is unaffected (208.5 ms before and after — dominated by materializing
  the 5,000 candidate dependents themselves, not by blocked-set computation). Re-measured after
  serializing the performance project's own test collections: 4.6 ms for `job_node_blocked()`,
  13.0 ms for `ExcludeBlocked=true`, and 199.9 ms for `ExcludeBlocked=false`. The guards are now
  deliberately discriminating: 25 ms for the stored function, 50 ms for exclusion, and 500 ms for
  the independently expensive include-blocked materialization. The stored-function test additionally
  asserts that `node_succeeded(id)` is evaluated from `CTE Scan on required` and rejects the old
  `Seq Scan on job_prerequisite ... node_succeeded(from_id)` plan, so a noisy machine cannot allow
  the per-edge shape to pass merely because wall time stayed below a broad ceiling. SQLite's
  `SqliteAwaitingProgressQueryPort.LoadBlockedNodes` was
  already in this shape (a `required`/`required_subtree`/`unsatisfied`/`blocked` CTE chain evaluating
  each required job once) before this finding — PostgreSQL's stored function now mirrors it, with the
  `MATERIALIZED` hint standing in for SQLite's lack of a query planner that would otherwise inline a
  CTE the same way.

**Deterministic performance lane (2026-07-28 fresh-eyes review §2.3).** Two Awaiting Progress
ceilings above were widened purely to absorb shared-PostgreSQL-instance contention from a full
`dotnet test JobTrack.slnx` run (the default-page ceiling to 1.5 s against a ~34 ms isolated figure,
the combined-tree ceiling to 2.5 s against a 744–1,285 ms isolated figure) — a runner-scheduling
concern, not a query regression, but widening the *ceiling* rather than fixing the *runner* let a
genuine 2×+ slowdown of either query pass undetected. `scripts/perf-test.sh` is now the one
deterministic lane: it cleans orphaned test databases, runs `JobTrack.Database.PerformanceTests`
alone (serialized, not concurrent with any other PostgreSQL-backed project), and cleans again
afterward. Every ceiling in `FullTableHierarchyLoadPerformanceTests` is measured and enforced against
that lane now, restored to isolated-evidence figures with headroom:

- `RealisticCombinedProductionTreeDefaultPageCeiling`: 1.5 s → 200 ms (isolated ~34 ms originally,
  77.8–111.7 ms re-measured via the serialized `scripts/perf-test.sh` lane on 2026-07-28). The
  ceiling is below twice the highest recorded warm measurement, so the review's deliberate-2×
  regression check fails as required.
- `CombinedProductionTreeAwaitingProgressCeiling`: 2.5 s → 1.5 s (isolated 744–1,285 ms originally,
  ~861–874 ms re-measured via `scripts/perf-test.sh` 2026-07-28).

Contention was reproduced deliberately (not assumed): running this same test alongside
`JobTrack.Persistence.PostgreSql.Tests`, `JobTrack.Database.ContractTests`, and
`JobTrack.Web.IntegrationTests` concurrently measured the default-page query at 177–259 ms (vs.
53–108 ms isolated) and the combined-tree query at 1,212.8 ms (vs. 861–874 ms isolated) — roughly
2–3×, consistent with every prior contention note in this file, and confirming the two ceilings above
were sized to the wrong lane. `JobTrack.Database.PerformanceTests` now sets `IsTestProject=false`, so
a solution-wide `dotnet test JobTrack.slnx` (including the "full solution suite" run) still compiles
it but silently skips its test execution entirely, rather than ever failing on contention — the full
suite must always be able to pass on its own; `scripts/perf-test.sh` (which overrides the property
back with `-p:IsTestProject=true`) is the only supported way to run this project's tests, and the
only source of evidence for a ceiling here going forward. A future ceiling increase requires a
before/after query plan and an explicit product-regression rationale (CLAUDE.md's commit-gate section
already says this) — "the shared test server was busy" is a runner defect, not grounds for widening a
query budget.

**Note on the broad-branch child listing row (revised 2026-07-23):** isolated measurement of
`Paginated_child_listing_of_a_10000_leaf_branch_meets_the_latency_and_plan_budget` is sub-millisecond
(0.8 ms), well inside the original 30 ms budget. Run as part of the full `dotnet test JobTrack.slnx`
solution suite, though, the same query was observed at 130 ms — every other test project (Web
integration/end-to-end, contract, PostgreSQL persistence) contends for the same local PostgreSQL
instance concurrently, and this is a wall-clock latency assertion, not a query-plan one (the
plan-shape assertions above it — index scan, no disk sort spill — are unaffected and still pass).
Revised to 200 ms, headroom above the measured contended case, following the same precedent as the
session-overlap row's revision in §3: the query is not slower, the shared test environment is
noisier when every PostgreSQL-backed project runs at once. This is also why the full solution suite
is not the routine commit gate (see the project's `CLAUDE.md` and the developer guide's "Fast core suite"
section) — `./scripts/fast-test.sh` plus a targeted `--filter` run is. The §2.3 zero-match search
ceiling (`Search_with_no_matches_at_combined_production_tree_scale_stays_within_ceiling`) hit the
identical contention when the full suite ran once at the close of the 2026-07-25 scalability-
follow-up plan (~450 ms contended vs. ~20 ms isolated) and was revised the same way, to 700 ms.

SQLite functional budget (not a latency target, since SQLite's single-writer envelope makes
head-to-head latency comparison misleading, §6.4): every operation above must complete without
indefinite blocking under SQLite's configured busy timeout, and a concurrent write attempt during
another writer's transaction must fail fast with the documented busy/locked error rather than hang.

**Stage 0 attribution profile (2026-08-08, large-database performance plan §4 Stage 0).** Temporary
in-process instrumentation (`Stopwatch` checkpoints around `CostSegmentPartitioner`'s
`EligiblePieces`/`Boundaries`/sweep phases and `CostEngine`'s rate-resolution loop, plus a
`CommandCountInterceptor` and `GC.GetTotalAllocatedBytes()`/`GC.CollectionCount` deltas), reverted
before commit per the plan's "temporary, not committed" instruction — not present in the working
tree. Measured via `./scripts/perf-test.sh`, warmed process, PostgreSQL only.

*Correcting the apples-to-oranges comparison §2.3 of the plan flagged.* The 2026-08-06 materialisation
plan's "~359-508 ms pure engine" figure came from manually replicating the engine with `Partition()` +
trace-producing `CostEngine.Calculate()` — a different, heavier call shape than any real aggregate-read
operation actually issues. Attributing the *real* per-operation call paths instead:

| Operation (long-history: 20 workers, 5-year window, ~36,500 sessions) | Total, warm | Engine share (partition+price) | DB/orchestration share |
|---|---|---|---|
| `GetCostDetailsAsync` (one leaf, trace path: `Partition`+`Calculate`) | 21-27 ms (cold first call 41 ms) | ~2.5 ms (eligiblePieces 0.7, boundaries 0.6, sweep 0.3, rateResolution 0.9; 1,825 sessions/pieces, one worker) | ~19-24 ms |
| `GetHierarchyTotalsAsync` (20-worker branch, aggregate path: `PartitionBounded`+`ComputeLeafCosts`) | 122-173 ms | ~32-36 ms (eligiblePieces 13-16, boundaries 7, sweep 4-5, rateResolution 7; 36,500 sessions/pieces across 20 workers) | ~90-140 ms |
| `GetBulkNodeCostsAsync` (branch as one bulk candidate: `Partition`+`ComputeLeafCosts`) | 148-178 ms | ~29-33 ms, same piece/boundary counts as above | ~115-150 ms |
| `GetRequesterVisibleHierarchyAsync` (branch, duration-only: `Partition`, no rate resolution) | 141-148 ms | ~16-25 ms (eligiblePieces 13-17, boundaries 2, sweep 3-6; no override/exception boundaries, so roughly half the boundary count of the cost paths) | ~120-125 ms |

**The engine is not dominant at any of the four real operation shapes.** DB materialization and
per-request orchestration (access checks, hierarchy aggregation, dictionary merges, the
`JobTrackOperation.TraceAsync` wrapper) account for 75-90% of end-to-end time on the aggregate paths,
and ~90% on the single-leaf trace path. The correctly-profiled engine share (~30-36 ms for a 20-worker,
36,500-session branch) is roughly 10-15x smaller than the previous mis-measurement. This does not
mean engine work is free — Stage 2's items remain candidates — but it reorders priority: DB
materialization (already addressed by the 2026-08-06 plan, further reducible by Stage 3's rollups) is
the larger term at this scale, not the pure engine.

At the **overlapping-cost scale** (50 workers x 400 leaves, dense short-window sessions), every
operation completes in 24-40 ms with an engine share of 0.1-2 ms — consistent with the existing
27x-headroom finding; no regression risk identified for any Stage 1-2 change evaluated against this
profile.

Allocation/GC figures (process-wide `GC.GetTotalAllocatedBytes()`, since per-thread allocation
counters are unreliable across `ConfigureAwait(false)` continuations that hop thread-pool threads):
the 20-worker branch aggregate read allocates ~105-110 MB and triggers 12-15 Gen0 and 2-4 Gen1
collections per call; Gen2 is rare (0-2 per call). Nothing observed here suggests LOH/Gen2 churn is
yet a binding concern at this scale, but the Gen0 rate (a dozen-plus collections per single aggregate
read) is consistent with Stage 2g's buffer-pooling candidate being worth a profile-gated look once
Stage 1/2 land. DB round trips are flat per operation shape (10-13 commands) regardless of worker
count, matching the existing bulk-path command-count guarantees.

**Concurrent-load baseline** (`GetHierarchyTotalsAsync`, 20-worker long-history branch, one shared
process-wide `NpgsqlDataSource` reused by every concurrent caller, matching production DI):

| Concurrency | Wall (all callers) | p50 | p95 | Throughput | CPU time | Working set |
|---|---|---|---|---|---|---|
| 1 | 343 ms | 343 ms | 343 ms | 2.9 req/s | 532 ms | 198 MB |
| 5 | 234 ms | 233 ms | 234 ms | 21.4 req/s | 859 ms | 389 MB |
| 10 | 623 ms | 620 ms | 623 ms | 16.1 req/s | 4,304 ms | 441 MB |
| 20 | 843 ms | 747 ms | 842 ms | 23.7 req/s | 3,633 ms | 690 MB |

Throughput does not scale linearly with concurrency (roughly flat from 5 to 20 simultaneous callers,
~16-24 req/s) while CPU time consumed grows close to linearly with concurrency and p50 latency more
than doubles from 1 to 20 callers. This machine has more cores than the deployed Cloud Run revision is
likely to be sized with, so the absolute throughput ceiling is not directly portable, but the *shape*
— CPU-bound saturation, not connection-pool or I/O starvation, since no pool-exhaustion errors or
latency cliffs occurred up to 20 concurrent callers against the default Npgsql pool size — supports the
plan's own caution: Stage 1's per-request parallelism must be bounded by a process-wide CPU bulkhead,
because it would compete with exactly this same external-request concurrency for the same CPU budget
this baseline shows is already the binding resource, not idle headroom. Working set grew from 198 MB
to 690 MB across the tested range without a cliff; peak memory was not the limiting factor in this run
at up to 20 concurrent long-history branch reads, though this baseline does not by itself rule out
memory as the binding constraint on a smaller deployed container.

**Conclusion for Stages 1-3 sequencing.** The corrected attribution weakens the original case for
prioritising Stage 2 (engine hot-path work) ahead of Stage 3: the engine is a minority term at every
measured operation shape, so Stage 2's constant-factor wins bound a smaller fraction of total latency
than assumed when this plan was written. Stage 1 (per-worker parallelism) still has a real target
(the ~30-36 ms engine share is itself embarrassingly parallel per ADR 0017), but the concurrent-load
baseline above shows CPU is already the scarce resource under realistic concurrent load, so Stage 1
must ship with the CPU bulkhead the plan already requires, not as an unconditional win. Stage 3
remains the only stage that flattens the unbounded axis (§3 of the plan) and targets the larger
(DB-materialization) term directly; nothing in this profile changes its own trigger condition
(§4 Stage 3 "when to trigger this stage").

**Stage 1 — withdrawn after its acceptance measurement (2026-08-08, large-database performance plan
§4 Stage 1).** The first implementation parallelized the aggregate per-worker loops through a static,
process-wide CPU bulkhead. A fresh-eyes review found that the bulk-cost loop had been omitted and, more
importantly, that the plan's mandatory post-change concurrent-load measurement had not been run. The
bulk omission was corrected, then the same temporary in-process 1/5/10/20-caller matrix used for Stage 0
was rerun against the 20-worker, five-year hierarchy-total shape. Results with the default degree:

| Concurrency | Wall (all callers) | p50 | p95 | Throughput | CPU time | Working set after |
|---|---|---|---|---|---|---|
| 1 | 198 ms | 197 ms | 197 ms | 5.0 req/s | 378 ms | 219 MB |
| 5 | 355 ms | 343 ms | 348 ms | 14.1 req/s | 2,052 ms | 300 MB |
| 10 | 671 ms | 602 ms | 668 ms | 14.9 req/s | 4,549 ms | 409 MB |
| 20 | 1,238 ms | 1,155 ms | 1,232 ms | 16.2 req/s | 9,577 ms | 843 MB |

Against Stage 0's baseline, throughput regressed from 21.4 to 14.1 req/s at five callers and from
23.7 to 16.2 req/s at twenty, while p95 increased from 234 to 348 ms and 842 to 1,232 ms respectively.
A second run capped each request at degree 2 did not recover the gate: 12.9/13.5/19.4 req/s at
5/10/20 callers, with p95 377/734/1,023 ms. The optimization therefore fails its explicit acceptance
condition even though it improves one isolated caller. Stage 1 is withdrawn: all worker loops are
sequential again, the bulkhead and deployment setting are removed, and cancellation is checked between
worker calculations. Temporary measurement instrumentation was reverted before commit. This evidence
also corrects the earlier close-out claim that the bulk path was parallelized and that the bulkhead's
design alone was sufficient proof against a concurrent-load regression.

**Stage 2 — engine hot-path work (2026-08-08, large-database performance plan §4 Stage 2).** Three
items shipped; four withdrawn with evidence, per the plan's own "candidate list, not a mandate."

*Shipped:*

- **2b (packed-array active set).** `CostSegmentPartitioner.PartitionCore`'s `SortedSet<int>` active
  set replaced with an `int[] active` / `int[] slotOf` swap-remove structure, matching the flat-array
  pattern the same file already applied to its other hot structures. This changes active-index
  iteration (and therefore allocation emission) order, which is safe: `CostEngine.Calculate` already
  re-sorts its trace under a total order, and `CostSegmentPartitionerPropertyTests` already
  canonicalizes (sorts) allocations before comparing, because `Partition` never promised an emission
  order. All 541 `JobTrack.Domain.Tests` (including the property-based oracle tests) pass unchanged.
- **2d (single-probe dictionary access).** `CollectionsMarshal.GetValueRefOrAddDefault` replaces
  `GetValueOrDefault` + indexer-set (two hash probes) in `CostEngine.ComputeLeafCosts`,
  `CostEngine.Calculate` (both the per-segment session-id grouping and the leaf-cost-amount
  accumulation), and `AllocatedDurationCalculator.ComputeLeafDurations`.
- **2f (struct enumerator for `IntervalIndex.Overlapping`).** Replaced the `yield return` iterator
  (one state-machine allocation per call -- 36,500 calls at the long-history scale, once per costed
  session) with a struct `OverlappingEnumerable`/`Enumerator` pair, still implementing
  `IEnumerable<WorkInterval>` for LINQ/test-assertion call sites (which box the struct only on that
  fallback path, never on the concrete `foreach` in `CostSegmentPartitioner.EligiblePieces` or
  `IntervalAlgebra.Subtract`). `IntervalIndexTests` and the interval-property tests pass unchanged.

Measured via the same manual `Partition`+`Calculate` sequential replication `LongHistoryScalePerformanceTests`
already used for the DB-vs-engine split (20 workers, 36,500 sessions, trace-producing -- deliberately
isolated from Stage 1's parallelism and from DB materialization, so this is a clean before/after of
Stage 2's engine-only changes): **167.7 ms → 114.4 ms, a ~32% reduction**, all three correctness suites
(`JobTrack.Domain.Tests`, `JobTrack.Application.Tests`, the four long-history/overlapping-cost
performance tests) passing unchanged. Whole-operation figures from the same run (e.g. branch hierarchy
totals) are not quoted as before/after evidence here: `./scripts/perf-test.sh`'s test-class discovery
order was not identical between the Stage 1 and Stage 2 runs, so whichever test ran first paid a
different share of process cold-start (EF model build, pool spin-up) each time, which would misattribute
warm-up noise to this stage's changes.

*Withdrawn with evidence:*

- **2a (fused aggregate-only sweep).** Not pursued. Stage 0's corrected attribution found the *whole*
  engine (partition + price + duration + aggregation) is only ~30-36 ms of a ~130-170 ms 20-worker
  branch read; 2a's target -- redundant iteration/materialization of the allocation list within that
  30-36 ms -- bounds a fraction of an already-small term. The item is also the largest, riskiest
  rewrite in this stage (a new internal aggregate code path alongside the existing trace path). Not
  worth its risk at the current measured scale; revisit if Stage 5's production telemetry shows
  aggregate reads growing enough to make this term matter again.
- **2c (compiled/cursor rate timelines).** Not pursued. Stage 0 measured rate resolution at ~7 ms of
  the ~30-36 ms engine share for the 20-worker branch -- material relative to the engine, immaterial
  relative to the operation. The plan's own gate ("if Stage 0 still shows rate resolution material")
  is a judgment call given the small absolute number; deferred rather than spending a binary-search/
  cursor rewrite of `RateResolver` on single-digit milliseconds.
- **2e (`SessionSegmentAllocation` as `readonly record struct`).** Not pursued. This is a public-API
  break to `JobTrack.Domain` requiring a `PublicAPI.Shipped.txt` update and every in-repo consumer
  changed in one commit. The plan explicitly says not to take this break "merely to optimize
  aggregate reads if 2a removes their allocation objects altogether" -- 2a itself was withdrawn above,
  so this precondition is doubly unmet; revisit only alongside 2a, not before it.
- **2g (pool large transient buffers).** Not pursued. Stage 0's GC figures (12-15 Gen0, 2-4 Gen1, 0-2
  Gen2 collections per 20-worker aggregate call; ~105-110 MB allocated) showed Gen0 churn but
  explicitly "nothing observed here suggests LOH/Gen2 churn is yet a binding concern at this scale."
  The plan's own caution applies directly: "pooling that saves nothing is complexity with a
  lifetime-bug surface." Revisit only if Stage 5's production telemetry or a future profile shows
  Gen2/LOH pressure under real concurrent load.

**Stage 3 — deliberately deferred (2026-08-08).** The plan's own §4 Stage 3 text is explicit: "Until
[the trigger fires], Stage 3 stays deliberately unbuilt and no cache schema is committed." The trigger
is "aggregate-only p95 cost-read latency exceeds half its budget or production window/session
telemetry crosses the measured knee in that curve." Post-Stage-2 measurements do not meet that bar:
long-history branch hierarchy totals (the aggregate operation Stage 3 would target) measured 165.7 ms
against its 500 ms budget (33%, not "half its budget" = 250 ms) in the most recent isolated run, and no
production telemetry exists yet to establish a knee (Stage 5b below is what will supply it). Building
the rollup schema, dirty-generation invalidation and backfill machinery now would be exactly the
"premature shape" the plan itself warns against. **Deferred, not skipped:** the trigger criterion above
is the record required by completion criteria item 7; Stage 5b's telemetry is what will let the trigger
fire on data rather than on someone remembering to re-check this file.

**Stage 4 — bounded-window reads (2026-08-08, large-database performance plan §4 Stage 4).** Audited
every call site of the four public cost-read members (`GetCostDetailsAsync`, `GetHierarchyTotalsAsync`,
`GetBulkNodeCostsAsync`, `GetRequesterVisibleHierarchyAsync`): `src/JobTrack.Web/JobTrackApi.Cost.cs`
(the external HTTP API), `src/JobTrack.Web/Pages/Jobs/CostReport.cshtml.cs`, and
`src/JobTrack.Web/Pages/Jobs/Work.cshtml.cs`. Every call site passes only `AsOf` -- none accepts or
computes a narrower start bound, confirming the plan's own prediction ("the public cost requests
expose only `asOf`, not a start bound"). Every one of these is a lifetime-style question ("this node's
cost as of now") by construction, not a genuinely period-scoped one ("this node's cost in March") --
there is no existing call site to convert. Per the plan's explicit instruction ("Do not invent a
monthly/date-ranged feature under this performance plan"), zero call sites were converted. This is the
stage's own predicted outcome, not a gap: lifetime totals cannot narrow this way, which is precisely
what a future Stage 3 (currently deferred, see above) would cover for aggregate reads.

**Stage 5a — write-path scaling (2026-08-08, large-database performance plan §4 Stage 5a).**
`WriteContentionPerformanceTests` covered contention shapes (concurrent session starts, overlapping
structural moves) but every existing scenario seeded a fresh worker/subtree with no prior history --
not the "does the same-user/same-leaf overlap rejection stay bounded when the worker already has a
large session history" question Stage 5a asks. Added
`Concurrent_same_user_same_leaf_session_start_rejection_meets_the_latency_budget_at_high_session_density`,
reusing the overlapping-cost scale's "heavy worker" fixture (5,000 sessions, the heaviest single-worker
density this codebase seeds) so the two racing inserts contend against a GiST exclusion-constraint
index already populated with that worker's full history, on a freshly inserted leaf isolated from the
branch's other 5,000 sessions. Passed within the existing 1.5 s `SessionRejectionBudget` -- the GiST
index (schema version 0007's `work_session_no_same_leaf_user_overlap`) scales with lookup depth, not
linearly with row count, so this is the expected result, now with regression coverage. `job_node_blocked`
recomputation bound (ADR 0051) already has dedicated coverage (`FullTableHierarchyLoadPerformanceTests`,
§2.4 above: 46.7 ms → 3.9 ms via `MATERIALIZED` CTEs) -- no new test added for that half of 5a per the
plan's own "if coverage already exists, record that and close the item" instruction.

**Stage 5b — growth-signal telemetry (2026-08-08, large-database performance plan §4 Stage 5b).**
`CostQueries` now logs one compact `cost_read_growth_signal` structured line per call across all four
public cost-read shapes (`GetCostDetailsAsync`, `GetHierarchyTotalsAsync`, `GetBulkNodeCostsAsync`,
`GetRequesterVisibleHierarchyAsync`), each wrapping its own DB-materialization call and engine/
aggregation work in separate `Stopwatch`es: `operation`, `db_ms`, `engine_ms`, `window_ticks` (the costed
`WorkInterval.Duration` as exact BCL-compatible ticks), `workers` (contributing worker count), `sessions_total`, and
`sessions_max`/`sessions_p50`/`sessions_p95` (per-worker session-count percentiles, sorted -- cheap at
the realistic tens-of-workers-per-request scale ADR 0017 scopes this to). **This is the exact
compact-subset/no-per-worker-array shape the plan specifies** and is the intended Stage 3 trigger
input (production window/session telemetry crossing a measured knee). Redaction is structural, not a
convention to remember: the log line's field set is fixed by a `[LoggerMessage]` template that only
ever receives enum/`long`/`int` values -- no rate, cost, node id, user id, or session id is ever
in scope to pass to it. `CostQueriesGrowthSignalTests` (`tests/JobTrack.Application.Tests`) proves this
for all four call shapes with both an exact-template redaction assertion and structured-property
assertions for operation, window ticks, worker count, total sessions and session percentiles --
matching post-1.0 Stage 2's own TDD requirement for a redaction test. Logging is optional and off by
default (a `null` logger everywhere except where explicitly wired): `JobTrackPostgreSql.
CreateWithPatDataSources` takes an `ILoggerFactory?`, wired from `JobTrack.Web`'s DI container.

Fresh-eyes correction (2026-08-08): `operation` distinguishes aggregate hierarchy, aggregate bulk,
requester-duration and trace-detail reads, so Stage 3's aggregate-only p95 trigger is derivable rather
than conflated with a query shape rollups cannot serve. For details and hierarchy totals, `db_ms` now
starts before `GetCostAccessInputsAsync` and stops after `GetCostInputsAsync`; it therefore includes all
port/authorization work, consistently with bulk authorization being part of `GetBulkCostInputsAsync`.
`window_ticks` supersedes the original `double window_days`, preserving the project's prohibition on
`double` along the duration path while retaining an exact, compact cost-window growth dimension.

**Stage 5c/5d/5e/5f (2026-08-08).**

- **5e (fixture economics) -- measured.** Temporary isolated timing in a fresh database per scale
  (per the plan's "not a subject for a product test" instruction, not committed), using the existing
  `SeedLongHistoryScaleAsync(..., days:)` parameter rather than adding fixture machinery: 1 year
  (20 workers x 365 days = 7,300 sessions), **209.0 ms**; 5 years (36,500 sessions), **1,305.1 ms**;
  10 years (73,000 sessions), **2,735.8 ms**. The previously recorded empty-database schema deployment
  was 98 ms. Seed cost scales approximately linearly and remains immaterial relative to the serialized
  performance lane even at ten years, so neither further generator optimization nor snapshot/template-
  database lifecycle machinery is warranted. Temporary measurement code was reverted before commit.
- **5c (PostgreSQL operating curve) -- not run.** Table/index size, autovacuum lag, backup/restore
  time and the forward-migration window at 5- and 10-year scales, and the partitioning/BRIN/covering-
  index evaluation need real backup/vacuum and production-volume operating evidence this session does
  not have access to exercise credibly
  (a `pg_dump`/restore timing or `pg_stat_user_tables` autovacuum-lag figure measured against a
  freshly seeded 1.7-second local database proves nothing about a production-scale install). Left open
  for a session with that infrastructure; the existing worker-leading indexes remain authoritative in
  the meantime, per the plan's own default.
- **5d (bounded worst case) -- not run.** Stage 5e proves the configurable generator can seed a
  ten-year shape cheaply, removing fixture economics as a blocker; the end-to-end worst-case and
  transaction-local timeout/failure-contract evaluation remain a distinct deferred measurement. No
  `statement_timeout` guardrail is adopted without that evidence.
- **5f (runtime configuration candidates) -- not run.** All three candidates (GC mode, ReadyToRun
  validation, Npgsql automatic preparation) require, per the plan's own instruction, "a container sized
  like the deployed Cloud Run revision" -- this session runs against a local development machine, not a
  deployment-matched container, so a measurement taken here would not answer the question the plan
  asks. Stage 0's attribution did not isolate DB parse/plan time specifically (its DB-vs-engine split is
  coarser than that), so the Npgsql auto-prepare candidate's own precondition ("if DB parse/plan time is
  material") is not yet established either. None screened out with evidence; none adopted. Left open for
  a deployment-matched environment.

**Plan close-out (2026-08-08, large-database performance plan §8 completion criteria).**

- **Long-history ceilings.** `LongHistoryScalePerformanceTests`'s `LeafCostBudget` (800 ms) and
  `BranchCostBudget` (500 ms) are left unchanged, deliberately not tightened further: warm-process
  measurements this session ranged from ~21 ms to ~617 ms for the leaf read and ~122-280 ms for the
  branch read depending on which test happened to run first in the process (paying first-call EF
  model build / connection-pool spin-up) -- exactly the cold/warm variance the existing ceiling
  comments already document. Tightening either ceiling to a warm-only figure would risk a flaky
  regression on whichever run genuinely pays the cold cost, which the plan's own revision policy
  ("real headroom," never asserted against best-case noise) argues against.
- **The original 150 ms leaf aspiration was not reached, and is not expected to be reachable by this
  plan's remaining unbuilt stage.** The leaf read's own cost is ~90% DB materialization/orchestration,
  not engine (Stage 0's corrected attribution), and DB materialization for a lifetime-style question
  grows with database age by construction (§3's "sessions per contributing worker" axis) -- Stages 1-2
  here made the engine faster but engine was never the leaf read's dominant term. Stage 3 (period
  rollups) is the only stage that could flatten the DB-materialization term for *aggregate* reads, and
  it explicitly cannot answer `GetCostDetailsAsync`'s trace-producing leaf read at all (per its own
  "Applicability" section) -- so even a built Stage 3 would not have closed this gap. The honest
  number: ~600 ms cold / ~20-40 ms warm, against the 800 ms ceiling; the 150 ms aspiration is retired,
  not met.
- **Overlapping-cost ceilings are unchanged or improved**: the full 29-test performance suite passes
  with every existing ceiling intact. The heavy-worker (5,000-session) figure was re-measured at 693.2 ms
  (previously 701.4 ms -- unchanged within run-to-run noise); it remains deliberately unbudgeted, per
  the overlapping-cost-scale plan's own §7 reasoning that this worst case has no corresponding
  performance-budgets.md row.
- **Stage 6 remains unexecuted.** Its own trigger ("after Stages 0-4, the profile still shows the
  partition sweep dominant") is not met -- Stage 0's corrected attribution found the opposite: the
  engine, including the sweep, was never the dominant term at any measured operation shape. No
  PostgreSQL-only cost algorithm is warranted.
- **Stage 3's deferral and Stage 5b's telemetry are both recorded above**, satisfying completion
  criterion 7: the trigger fires on production data (once deployed), not on someone remembering to
  re-check this file.
- **This plan is substantially, not fully, complete.** Stages 0, 2 and 4 are fully delivered; Stage 1
  is withdrawn with the concurrent-load evidence above; Stage
  5's 5a/5b/5e are delivered, 5c/5d/5f are explicitly deferred (not withdrawn) pending their remaining
  operating-curve, worst-case and deployment-matched measurements. `2026-08-06-post-1.0-improvement-plan.md` Stage 4 is annotated as
  delegated here.

## 3. High-concurrency / write-contention budgets

| Operation | Scale | Budget |
|---|---|---|
| Concurrent same-user/same-leaf session start attempts (should reject all but one) | 2 simultaneous connections | Loser observes the stable overlap-rejection error within 1.5 s of the winner's commit (revised from 200 ms — see note below) |
| Structural move under advisory lock (ADR 0012) contention | 10 simultaneous move attempts on overlapping subtrees | No deadlock; total serialized completion within 2 s |
| Bootstrap race (ADR 0015) | 5 simultaneous bootstrap attempts | Exactly one succeeds within 500 ms; the other four observe the stable "already bootstrapped" error, no partial writes |
| Advisory-lock deadlock-avoidance ordering test (ADR 0012) | 2 opposing-order move requests | No deadlock detected by PostgreSQL; both requests complete via serialization, not error |

**Note on the session-overlap row (revised in the §6.7 race-test/performance-test work):** measured
against `work_session_user_range_gist_idx` (the GiST exclusion constraint enforcing
same-user/same-leaf non-overlap, schema slice 7), the loser's rejection latency is bimodal —
roughly 400 ms in most interleavings, but consistently ~1.08 s (never anywhere in between) whenever
the two connections' inserts land close enough together that the loser blocks on the winner's
in-flight row rather than observing it already committed. That ~1.08 s matches this instance's
`deadlock_timeout` (1 s) plus overhead almost exactly: GiST exclusion-constraint conflicts under
concurrency are a documented PostgreSQL case where the waiting inserter is only unblocked by the
periodic deadlock-detector cycle, not by immediate lock release on commit, even though no true
deadlock exists. Serializing same-user/same-leaf session starts behind an ADR-0012-style advisory
lock would avoid this, but that trades unconditional latency and added lock-domain complexity for a
low-frequency race that already resolves correctly, just slower than first estimated — not a
worthwhile trade for this operation. The budget is revised to 1.5 s (headroom above the measured
~1.08 s worst case) rather than the design changed to chase the original 200 ms.

## 4. Review and revision policy

- These budgets are re-measured, not re-guessed, once the corresponding schema slice (§6.2) and
  canonical query (§6.5) exist — the database gate (§6.7) is where a budget is actually enforced.
- A budget that measurement shows to be unachievable without a design change is revised here with a
  one-line rationale (what changed and why), cross-referenced from the PR that revises it; it is
  never silently dropped from the gate's test suite.
- New operations added after M0 (a canonical query not anticipated here) get a new row before their
  owning schema/application slice's tests are written, following the same maintenance discipline as
  `docs/traceability/test-catalogue.md`.
