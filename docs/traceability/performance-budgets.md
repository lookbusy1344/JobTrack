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
| Cost calculation for one leaf, single `asOf` | Long history | 150 ms | Canonical cost-input query (§6.5) plan uses the temporal indexes on `work_session`, schedule, and rate ranges; no nested-loop over the full history |
| Cost calculation for one branch (100 leaves), single `asOf` | Long history × Broad tree | 2 s | Batched cost-input materialization, not N+1 per-leaf queries |
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
`docs/plans/2026-07-09-overlapping-cost-scale-plan.md`. The original two rows against the **long
history** scale remain deferred — that scale (5 years of daily `work_session`/schedule exceptions for
20 users) targets historical `asOf`-range recalculation and re-validation, a different concern from
the overlapping-cost scale's per-worker concurrency-depth focus, and its generator is still not
built; the deferral is intentionally not closed by this plan (plan §3 non-goals). The "upgrade from
oldest supported version" schema-deployment row is also deferred: constructing it faithfully means
deploying only the earliest schema versions, seeding combined-production-tree scale, then applying
every remaining version — disproportionate scaffolding for one budget row at this stage. All other
rows in this table, plus every row in §3, are covered by `JobTrack.Database.PerformanceTests`.

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
is not the routine commit gate (see the project's `CLAUDE.md` and README's "Fast core suite"
section) — `./scripts/fast-test.sh` plus a targeted `--filter` run is. The §2.3 zero-match search
ceiling (`Search_with_no_matches_at_combined_production_tree_scale_stays_within_ceiling`) hit the
identical contention when the full suite ran once at the close of the 2026-07-25 scalability-
follow-up plan (~450 ms contended vs. ~20 ms isolated) and was revised the same way, to 700 ms.

SQLite functional budget (not a latency target, since SQLite's single-writer envelope makes
head-to-head latency comparison misleading, §6.4): every operation above must complete without
indefinite blocking under SQLite's configured busy timeout, and a concurrent write attempt during
another writer's transaction must fail fast with the documented busy/locked error rather than hang.

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
