# The cost engine

How JobTrack turns recorded work sessions into money: the boundary-partition algorithm, the `1/N`
concurrency rule, the PostgreSQL indexes that make the inputs cheap to find, and the EF Core loading
that keeps a cost read's work proportional to the query, not the size of the database.

Normative source: [`jobtrack_spec_codex.md`](jobtrack_spec_codex.md) §10. Entity-level background:
[`database-entities.md`](database-entities.md). The per-worker hourly rate this engine multiplies by
is resolved separately — see [`rate-resolution.md`](rate-resolution.md). Decisions:
[ADR 0002](decisions/0002-penny-reconciliation.md),
[ADR 0009](decisions/0009-decimal-precision-and-allocation.md),
[ADR 0017](decisions/0017-costing-read-scope.md),
[ADR 0018](decisions/0018-invalid-overlap-cost-engine.md),
[ADR 0053](decisions/0053-allocated-duration-accompanies-actual-cost.md).

---

## 1. The problem

Cost is recomputed on every read from the current rates, schedules, hierarchy, and sessions, against
a single `asOf` instant captured at the start of the operation, and never stored. Change a historical
rate and historical reports change with it: cost figures are live derivations, not accounting
entries.

The hard part is concurrency. One worker may have several sessions running at once, on different
leaves. Their time cannot be summed: a worker who runs three sessions for an hour has worked
one hour, not three. The spec's rule is that at every eligible instant, a worker's cost is divided
equally among their active sessions — each gets `1/N` of that instant. `N` has no upper bound and
must not be computed pairwise.

Two consequences drive the whole design:

- **`N` is a database-wide property of the worker, not of the requested job.** Costing a single leaf
  requires knowing about that worker's sessions on jobs the caller cannot even see (ADR 0017). Those
  foreign sessions influence the answer but never appear in it.
- **Equal shares of *time* are not equal shares of *money*.** Each session's rate resolves
  independently against its own node's override chain, so three sessions sharing an hour three ways
  can produce three different amounts.

---

## 2. A worked example

One worker, Dana, whose effective working set for the day is `[09:00, 17:00)`. Four sessions, on
four different leaves. `S4` was left running past the end of the working day.

```
              09:00 10:00 11:00 12:00 13:00 14:00 15:00 16:00 17:00 18:00
              |     |     |     |     |     |     |     |     |     |
working set   [===============================================)
S1 -> leaf A  [=======================)
S2 -> leaf B        [=============================)
S3 -> leaf C              [=====)
S4 -> leaf D                                [=======================)

N =           |  1  |  2  |  3  |  2  |  1  |  2  |     1     |  -  |
```

Brackets are literal: every interval in JobTrack is half-open, `[start, end)`. A session ending
exactly when another starts does **not** overlap it — the touching boundary belongs to the later
session alone.

### 2.1 Clipping to eligible time

Before any partitioning, each session is intersected with the worker's effective working intervals
(the expanded historical schedule, plus and minus any schedule exceptions). `S4` runs to 18:00 but
only earns to 17:00:

```
S4 recorded                                 [=======================)
S4 eligible                                 [=================)
```

Leaving a session running overnight generates no overnight cost unless a priced additive exception
covers that time. The last hour of `S4` is discarded.

### 2.2 Boundaries and segments

The engine collects every instant at which *anything* relevant could change — session edges, rate
edges, schedule-exception edges, working-interval edges — sorts them, deduplicates them, and sweeps
between consecutive boundaries. Each resulting **segment** has constant active-session membership
and a constant applicable rate, so within it the arithmetic is a single multiplication.

| Segment | Active sessions | `N` | Share each |
|---|---|---|---|
| `[09:00, 10:00)` | S1 | 1 | 1 h |
| `[10:00, 11:00)` | S1, S2 | 2 | ½ h |
| `[11:00, 12:00)` | S1, S2, S3 | **3** | ⅓ h |
| `[12:00, 13:00)` | S1, S2 | 2 | ½ h |
| `[13:00, 14:00)` | S2 | 1 | 1 h |
| `[14:00, 15:00)` | S2, S4 | 2 | ½ h |
| `[15:00, 17:00)` | S4 | 1 | 2 h |
| `[17:00, 18:00)` | — | 0 | outside the working set; no segment emitted |

Note the last two rows. `[15:00, 17:00)` is a *single* segment even though it spans two hours —
nothing changes at 16:00, so no boundary exists there. Segments are maximal by construction, never
fixed-width buckets. And `[17:00, 18:00)` produces nothing at all: the working-set clip removed
`S4`'s only claim on it.

### 2.3 Allocated duration

Summing each session's shares:

| Session | Leaf | Elapsed | Allocated | Lost to sharing / clipping |
|---|---|---|---|---|
| S1 | A | 4 h | `1 + ½ + ⅓ + ½` = **2⅓ h** | 1⅔ h shared away |
| S2 | B | 5 h | `½ + ⅓ + ½ + 1 + ½` = **2⅚ h** | 2⅙ h shared away |
| S3 | C | 1 h | `⅓` = **⅓ h** | ⅔ h shared away |
| S4 | D | 4 h | `½ + 2` = **2½ h** | ½ h shared, 1 h clipped |
| | | | **8 h total** | |

The total is exactly 8 hours — the length of the eligible working set that had at least one session
active (`[09:00, 17:00)`). Allocation redistributes a worker's day; it never invents or destroys it.
That conservation is the whole point.

Those thirds are why `AllocatedShare` is the **unreduced pair `(segmentTicks, N)`** and never a
rounded tick count. Rounding ⅓ h to whole ticks three times loses a tick, and the day stops
summing to 8 hours. See [ADR 0009](decisions/0009-decimal-precision-and-allocation.md) and
[ADR 0053](decisions/0053-allocated-duration-accompanies-actual-cost.md).

### 2.4 Rates, resolved per session

Each session's rate is resolved independently, at each segment's start, by `RateResolver.Resolve` —
the calculated cost per hour for this worker on this node at this instant, through the rota, the
effective-dated tables, and the nearest-ancestor override walk. That whole machinery is its own
subject: see **[rate-resolution.md](rate-resolution.md)**. The engine just asks it for one rate per
segment.

Give Dana a default rate of £60/h, with node overrides of £90/h on leaf B and £55/h on leaf C:

| Session | Rate | Per-segment contributions | Exact cost |
|---|---|---|---|
| S1 (A) | £60 | `60` + `30` + `20` + `30` | £140.00 |
| S2 (B) | £90 | `45` + `30` + `45` + `90` + `45` | £255.00 |
| S3 (C) | £55 | `55 ÷ 3` | £18.333333… |
| S4 (D) | £60 | `30` + `120` | £150.00 |

Look at `[11:00, 12:00)`, the three-deep segment. All three sessions receive an identical ⅓ hour,
and they earn £20.00, £30.00, and £18.33 respectively. Equal time, unequal money — exactly as the
spec requires.

Each figure is one division, computed once: `rate × segmentTicks ÷ (N × ticksPerHour)`, straight to
`decimal`. Never `round(share) × rate`, which would reintroduce the error the exact
`(segmentTicks, N)` pair exists to avoid.

### 2.5 Rate edges are boundaries too

Suppose Dana's `UserCostRate` rises from £60 to £72 effective 11:30. That instant becomes a boundary,
and the three-deep segment splits:

```
              11:00       11:30       12:00
              |           |           |
S1 -> leaf A  [===========|===========)
S2 -> leaf B  [===========|===========)
S3 -> leaf C  [===========|===========)
              |    N=3    |    N=3    |
              |   £60/h   |   £72/h   |      (S1 only; B and C have overrides)
```

`N` is unchanged at 3 across both halves, but S1's contribution becomes `60 × ⅙` + `72 × ⅙` =
£10.00 + £12.00 = £22.00, up from £20.00.

Two points. The split is **global to the worker**, not local to S1 — S2 and S3 are also partitioned
at 11:30 and each emits two allocations, even though their overrides make both halves price
identically. And the boundary set includes every rate-override edge on a session's node *and on every
one of its ancestors*, not merely the ancestor that currently wins: a distant override can start
applying the instant a nearer one lapses.

### 2.6 Rolling up the hierarchy

Place the four leaves under two branches:

```
root  £563.333333…
├── Alpha  £395.00
│   ├── leaf A   £140.00      2⅓ h
│   └── leaf B   £255.00      2⅚ h
└── Beta  £168.333333…
    ├── leaf C   £18.333333…  ⅓ h
    └── leaf D   £150.00      2½ h
```

`HierarchicalCostAggregator` walks this iteratively, not recursively — hierarchy depth is a data
property and must not be bounded by the call stack. A leaf's cost is the sum of its own sessions'
contributions; a branch's is the sum of its descendant leaves'; the root's is everything requested.
Allocated duration rolls up the same way, through `HierarchicalAllocatedDurationAggregator`.

Rounding happens only here, at the reporting boundary, midpoint-to-even. Because independently
rounding each node can leave a displayed parent disagreeing with the sum of its displayed children
by a penny, a report showing several levels at once passes through
[ADR 0002](decisions/0002-penny-reconciliation.md)'s reconciliation instead: round the children
naively, compute the residual against the parent's own rounded total, and assign the whole residual
to the single child whose naive rounding moved furthest in the cancelling direction (ties broken by
node id). Two children each at £18.333333… display as £18.33 and £18.34 against a parent of £36.67 —
never £18.33 and £18.33 against £36.67.

### 2.7 What the caller is allowed to see

Dana's sessions on leaves outside the requested subtree still count toward every `N` above. They are
loaded under an internal elevated read scope (ADR 0017) and are then stripped: `CostEngine`'s
`TryNarrowToExposed` drops any session identifier whose own node is absent from the result's cost
map, so a caller scoped to `Beta` receives correct, concurrency-reduced figures for C and D and
learns nothing about A, B, their rates, or their owners.

The accepted residual is an inference, not a disclosure: a lower-than-naïve cost implies the worker
was busy elsewhere. That is documented and accepted, because the alternative is reporting a figure
known to be wrong.

### 2.8 When the input is corrupt

Same-worker, same-leaf overlap is invalid — it would double-count one leaf's own time. The database
rejects it at write time (§3.1). If it nonetheless reaches the engine, via a raw write or an
out-of-band edit, `CostSegmentPartitioner` refuses rather than guessing: it throws
`InvariantViolationException` with `ConstraintId` `work-session.same-user-leaf-overlap` and the two
offending session ids. Silently allocating would double-count; silently dropping one would
under-report recorded labour. Both are worse than refusing ([ADR 0018](decisions/0018-invalid-overlap-cost-engine.md)).

---

## 3. The PostgreSQL side

### 3.1 `session_range`: one generated range column

`work_session` stores `started_at` and `finished_at` as ordinary `timestamptz` columns — those remain
the values of record — and derives a **`tstzrange` from them as a `STORED` generated column**
(`database/postgresql/schema-versions/0007_work-session.sql`):

```sql
started_at    timestamptz NOT NULL,
finished_at   timestamptz,
session_range tstzrange GENERATED ALWAYS AS (
    tstzrange(started_at, COALESCE(finished_at, 'infinity'::timestamptz), '[)')
) STORED
```

Three properties:

- **`'[)'` matches the domain.** The database's overlap operator and `WorkInterval`'s algebra agree
  on half-open semantics, so boundary-touching sessions behave identically in SQL and in C#.
- **An unfinished session is unbounded above**, not null and not a sentinel repeated at call sites.
  An open session therefore overlaps everything after it, automatically, in every range predicate.
- **`GENERATED ALWAYS` makes drift impossible.** Writes touch only `started_at`/`finished_at`;
  writing `session_range` is an error. No trigger, no application-side synchronisation obligation,
  no second source of truth. It is an index-support artefact that happens to be a column, and it
  never leaves the database — nothing in `Abstractions` or `Domain` models a `tstzrange`.

`STORED` is not optional here: PostgreSQL cannot index a virtual generated column, and the whole
point is the index.

That column carries the **exclusion constraint** that makes §2.8's corruption case a genuine
edge case rather than a routine one:

```sql
CONSTRAINT work_session_no_same_leaf_user_overlap
    EXCLUDE USING gist (
        worked_by_user_id WITH =,
        leaf_work_id      WITH =,
        session_range     WITH &&
    )
```

`btree_gist` supplies the `=` strategy for the two `bigint` equality terms; GiST natively covers only
the range term. `leaf_work_id` is a full equality term rather than part of the range because
same-worker overlap across *different* leaves is not merely allowed but is the input the entire cost
engine exists to process. Overlap is thus a constraint, not just a query convenience.

A partial unique index sits alongside it:

```sql
CREATE UNIQUE INDEX work_session_one_active_per_leaf_user_idx
    ON work_session (leaf_work_id, worked_by_user_id)
    WHERE finished_at IS NULL;
```

This is belt-and-braces with a specific purpose. The Phase 0 spike (`spikes/sql/03-gist-overlap.sql`)
found that the exclusion constraint's concurrent-conflict path surfaces as a PostgreSQL deadlock
(`40P01`) rather than a clean exclusion violation (`23P01`). The common case — starting a second
session on a leaf you already have open — hits the partial unique index first and gets a plain,
non-deadlocking `23505`.

### 3.2 The index, and the sargability trap

```sql
CREATE INDEX work_session_user_range_gist_idx
    ON work_session USING gist (worked_by_user_id, session_range);
```

Worker-leading, because every cost read starts from "this worker, this window".

Overlap discovery runs against the generated range column:

```sql
WHERE s.worked_by_user_id = p_user_id
  AND s.session_range && tstzrange(p_query_start, p_query_end, '[)')
```

The worker equality and the range overlap go into `work_session_user_range_gist_idx` as a single
index condition. On a 5,000-session worker spanning ~208 days, queried with a 10-hour window, this
reads 4 blocks in 0.009 ms.

The column-wise form of the same predicate is the trap:

```sql
-- NOT sargable against any range index
WHERE s.worked_by_user_id = p_user_id
  AND s.started_at < p_query_end
  AND (s.finished_at IS NULL OR s.finished_at > p_query_start)
```

The `OR`/`NULL` test on a *different* column from the range bound blocks the planner from pushing the
temporal condition into any index — not the GiST index, not the `(worked_by_user_id, started_at)` /
`(worked_by_user_id, finished_at)` btree composites. It degrades to an index scan keyed on
`worked_by_user_id` alone plus an in-memory filter over the worker's whole history, growing with
session count. The two forms are equal — two half-open intervals `[a,b)` and `[c,d)` overlap iff
`a < d ∧ c < b`, and substituting `session_range`'s definition yields the column form — so the range
form is the one to write.

Two caveats. The GiST plan wins once statistics are accurate; on a freshly bulk-loaded table with no
`ANALYZE`, stale estimates can still make a btree composite look cheaper, so
`PerformanceScaleGenerator` runs an explicit `ANALYZE` after seeding. And at very low selectivity — a
query spanning most of a worker's history — a plain index scan on `worked_by_user_id` beats GiST,
because there is nothing to prune. Both plans are acceptable; a sequential scan is not, and the
budget in [`traceability/performance-budgets.md`](traceability/performance-budgets.md) is written
that way.

### 3.3 Why this lives in a stored function

`worker_overlapping_sessions(p_user_id, p_query_start, p_query_end, p_as_of)` is one of the few
sanctioned exceptions to the EF-first rule ([ADR 0010](decisions/0010-ef-core-data-access.md)),
which names database-wide overlap discovery and the canonical cost-input queries explicitly. EF
cannot express `&&` against a generated range column in a way the planner will use, and the
alternative — an inline SQL string beside the call site — is what the house style forbids. The
function is source-controlled under `database/postgresql/schema-versions/`, and
`InlineDmlArchitectureTests` enforces that it is invoked *through* EF rather than duplicated.

It exposes both the raw `finished_at` and a computed `effective_finished_at`, and does **not** clip
`finished_at` to `asOf` itself. The caller selects the raw column and applies
`SessionEndClipping.ClipEnd` in C#, so one clipping rule serves both providers.

### 3.4 Supporting structures

| Structure | Purpose |
|---|---|
| `work_session_user_range_gist_idx` | Worker-leading overlap discovery — the hot path (§3.2) |
| `work_session_user_started_at_idx`, `..._finished_at_idx` | Ordering and boundary scans; the low-selectivity fallback plan |
| `work_session_leaf_work_id_idx` | The subtree join that discovers *which* workers contributed |
| `job_node_subtrees(root_ids[])` | Set-returning descendant expansion (schema v0013) |
| `job_node_ancestor_chains(node_ids[])` | Set-returning upward walk, for override resolution |

### 3.5 SQLite

SQLite has no exclusion constraints and no range type. `work_session` there enforces same-worker,
same-leaf non-overlap with `INSERT`/`UPDATE` triggers plus the same partial unique index, and reads
go through plain `(worked_by_user_id, started_at)` / `(…, finished_at)` composites. The shared
contract-test suite holds both providers to identical behaviour; only the plans differ.

The R\*Tree module could in principle index intervals there, but it is a virtual table — a
denormalised shadow structure requiring trigger-maintained synchronisation, keyed on `float64`
coordinates, which collides with the no-`double`-on-the-duration-path rule. SQLite is the embedded
and demo provider; adding a consistency risk to buy speed it does not need is the wrong trade. See
[`operations/sqlite-limitations-and-configuration.md`](operations/sqlite-limitations-and-configuration.md).

---

## 4. EF Core materialization

`PostgreSqlCostQueryPort` builds the engine's entire input in one snapshot, then hands a fully
materialized immutable structure to a pure function. The engine performs no I/O and no authorization
filtering; the port performs no arithmetic.

That split is why the engine is synchronous. `async`/`await` sits only at the I/O edge: the port's
queries in `Persistence.PostgreSql`/`Persistence.Sqlite`, awaited once by `CostQueries` before any
arithmetic. `JobTrack.Domain` — `CostEngine`, `CostSegmentPartitioner`, the aggregators,
`RateResolver` — holds no `async`. It is CPU work over materialized inputs; wrapping it in tasks
would buy scheduling overhead and no concurrency. `CostQueries.CalculateAsync` awaits the port, then
runs the engine straight-line on the same thread, never on `Task.Run` or the pool. The per-worker
helpers stay `static` and self-contained, so a measured bottleneck could be parallelised later
without a rewrite — none is offloaded now. `ConfigureAwait(false)` goes on every library await and
none in the hosts, enforced by each library's `.editorconfig`. The `Stopwatch` split behind the
`cost_read_growth_signal` log times the DB and engine halves apart, because they scale differently.

### 4.1 One snapshot, one context

Every cost read opens a single `DbContext`, begins a `RepeatableRead` transaction
(`PostgreSqlCostQuerySnapshot`), issues every query inside it, and commits before returning. A cost
figure assembled from a dozen queries across shifting snapshots would be internally inconsistent —
sessions from one instant, rates from another. Everything is read-only and `AsNoTracking`; nothing
here should ever pay for change tracking or identity resolution.

### 4.2 Load only the records a query needs

A cost read loads exactly two things, both through set-returning functions:

- **`job_node_subtrees(rootIds)`** — the requested roots' own subtrees.
- **`job_node_ancestor_chains(nodeIds)`** — extends that node map with the ancestor chains ADR 0017's
  elevated scope requires: each requested root's own path to the true root (an override may be
  declared above the requested subtree), and, for any contributing session on a leaf *outside* the
  requested subtree, that leaf's path to the root, because `RateResolver` walks every session's own
  ancestor chain for the nearest override.

Rows loaded scale with the query, not the size of the database:

| Scale | `job_node` rows | Nodes loaded | Latency |
|---|---|---|---|
| Broad tree | 10,002 | 3 | 7.1 ms |
| Combined production tree | 193,570 | 7 | 100.9 ms |

The residual latency is fixed per-request overhead — connection, snapshot, role lookup.

### 4.3 Set-based, never N+1

Worker session discovery is one command regardless of how many workers contributed, via a lateral
join over an unnested array:

```sql
FROM unnest({workerIds}) AS worker_ids(worker_id)
CROSS JOIN LATERAL worker_overlapping_sessions(
    worker_ids.worker_id, {boundsStart}, {boundsEnd}, {asOf}) AS sessions
```

The bulk path holds the same shape: `GetBulkCostInputsAsync` materializes one snapshot for a whole
listing page. The contract test asserts that pricing 200 candidates uses **the same command count as
pricing one**, with at most one concurrently open connection — a structural assertion, not a latency
one, so an accidental N+1 fails the build rather than merely slowing it.

### 4.4 Join server-side; don't round-trip id sets

Once the subtree is materialized, the obvious next step is to filter subsequent queries by
`= ANY(subtreeIds)`. That ships a potentially 50,000-element array back to the server and gives the
planner an opaque parameter instead of a join. The worker-discovery and node-override queries
instead re-invoke `job_node_subtrees` server-side and join against it, so the parameter shrinks from
O(subtree size) to O(requested root count):

```sql
FROM work_session ws
JOIN (SELECT DISTINCT id FROM job_node_subtrees({rootIds})) AS subtree
  ON subtree.id = ws.leaf_work_id
WHERE ws.started_at < {asOf}
GROUP BY ws.worked_by_user_id
```

That query also does double duty: the grouped `MIN(started_at)` establishes the calculation's lower
bound in the same pass that discovers which workers contributed. `bounds` is
`[earliest contributing start, asOf)` — every subsequent load is filtered to that window, so a
worker's decade-old sessions are never fetched to cost a job started last week.

### 4.5 Project narrowly where row counts are large

Most per-worker loads here return tens of rows, where entity shaping is irrelevant. One does not:
`schedule_exception` reaches 36,500 rows at the long-history scale. Projecting it to the five
columns it consumes measured **50.4 ms wide versus 9.7 ms narrow**. The rule that follows is
not "always project" — it is "project where the row count makes shaping visible next to the query",
and this is the only load in the method that qualifies.

Schedule expansion and exception resolution stay in the domain (`ScheduleExpander`,
`ScheduleExceptionResolver`), applied by the port over raw historical rows. The schedule
prefilter widens its window by one day at each end before comparing civil `LocalDate` bounds to
`Instant` bounds — no zone's offset exceeds 24 hours, so the widened window cannot exclude a version
that could produce a working interval inside `bounds`, and `ScheduleExpander` clips exactly per-zone
downstream regardless.

### 4.6 Index once, query many

Two structures inside the engine exist purely to stop scan lengths growing with the costed window
rather than with the answer:

- **`IntervalIndex`** — a sorted, binary-searchable index over the working set, built once per
  calculation. A worker's schedule expands to roughly one interval per working day between their
  earliest session and `asOf`, so the previous linear probe made an ageing installation
  progressively slower at answering an unchanged question. It detects non-disjointness at build time
  and falls back to a full scan when a public caller supplies overlapping intervals — same results,
  different cost.
- **`RateResolver.IndexOverridesByNode` / `FilterPricedExceptions`** — computed once per allocation
  set rather than once per allocation. Rate resolution runs per segment per session; regrouping the
  same unchanging overrides and rescanning an overwhelmingly-unpriced exception list each time
  dominated the arithmetic itself.

`CostEngine.ComputeLeafCosts` exists for the same reason at a coarser grain: per-segment rate
resolution runs **once per worker** no matter how many candidate roots need that worker's
contribution, leaving only the cheap tree walk to repeat per root.

---

## 5. Reading the code

| Stage | Type |
|---|---|
| Input materialization (PostgreSQL) | `src/JobTrack.Persistence.PostgreSql/PostgreSqlCostQueryPort.cs` |
| Boundary partition, `1/N` shares | `src/JobTrack.Domain/Costing/CostSegmentPartitioner.cs` |
| The exact `(segmentTicks, N)` pair | `src/JobTrack.Domain/Costing/AllocatedShare.cs` |
| Rate precedence | `src/JobTrack.Domain/Rates/RateResolver.cs` |
| One rounded division per segment | `src/JobTrack.Domain/Costing/SegmentCostCalculator.cs` |
| Aggregation, trace, ADR 0017 narrowing | `src/JobTrack.Domain/Costing/CostEngine.cs` |
| Hierarchy roll-up | `src/JobTrack.Domain/Costing/HierarchicalCostAggregator.cs` |
| Penny reconciliation | `src/JobTrack.Domain/Costing/HierarchyDisplayReconciler.cs` |
| Interval search structure | `src/JobTrack.Domain/Intervals/IntervalIndex.cs` |
| Schema | `database/postgresql/schema-versions/0007_work-session.sql`, `0018_worker-overlapping-sessions-sargable-range.sql` |
| Original spike | `spikes/sql/03-gist-overlap.sql` |
