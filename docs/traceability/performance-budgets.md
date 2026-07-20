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
| Broad-branch child listing (paginated, 50 rows/page) | Broad tree | 30 ms | Index scan on parent-id, no sort spill to disk |
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
