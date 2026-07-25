# Code Review and Scalability Remediation Plan

**Date:** 2026-07-24
**Status:** Implemented (2026-07-25) — every finding in §2 is Done or Resolved; see §3's four stages
for the evidence trail.
**Scope:** Full-project fresh-eyes review: architecture shape, read-path scalability, maintainability
hotspots, and plan-status hygiene. This plan records only current findings; it does not reopen
implemented product plans or accepted phase gates, and it deliberately does not restate the
2026-07-23 architecture boundary findings (see §2.1, which is about that plan's *status*, not its
content).

## 1. Review verdict

The architecture is sound and unusually well-enforced:

- Strict acyclic layering (`Abstractions` → `Domain` → `Application` → providers → hosts), with
  `ReusableLibraryDependencyTests` and the public-surface architecture tests keeping it true.
- Functional core is real, not aspirational: the cost engine, hierarchy calculators, and readiness
  logic are pure, exhaustively switch-matched, and fed by ports that do nothing but load and
  translate. Money never touches `double`; time never leaves Noda Time inside the domain.
- Database invariants are enforced in the database (single-root partial unique index, cycle-check
  trigger, GiST no-same-leaf-overlap exclusion, `move_job_node` stored function), not just in C#.
- The web host is hardened well beyond what an internal tool usually gets: fail-closed forwarded
  headers and data-protection config, strict CSP with no inline script, per-user API rate
  limiting, Kestrel body-rate defense, PRG discipline, dual cookie/PAT auth behind one policy
  scheme.
- Dual-provider parity rests on shared contract-test bases plus provider-specific race tests —
  the right mechanism for keeping two implementations honest.

No correctness defects were found in this review. The findings below are a scalability ceiling
that is currently deliberate but should have an explicit trigger and escape route, several
maintainability hotspots, and one piece of documentation drift that the project's own review
checklist treats as load-bearing.

## 2. Findings

Severity: **High** > **Medium** > **Low**.

### 2.1 The implemented 2026-07-23 architecture plan still said "Proposed" — RESOLVED 2026-07-24

| | |
|---|---|
| **Severity** | **High** (process, not code) |
| **Category** | Plan-status hygiene / gate evidence |
| **Files** | `docs/plans/2026-07-23-architecture-boundary-remediation-plan.md`, `docs/plans/README.md` |
| **Status** | **Fixed.** All four stages verified implemented against the plan's own §4 completion criteria; status block and index row both updated to `Implemented` with the evidence recorded inline in the plan. |

The 2026-07-23 plan's stages appear substantially implemented: `IWorkCommands.
FinishSessionAndUpdateWriteUpAsync` and its `/finish-and-update-write-up` endpoint exist (stage 1),
`IAccountCredentialCommands.ChangeOwnPasswordAsync` exists (stage 2), the application SPI ports are
`internal` per ADR 0049 (stage 3), and the guardrail suites `OneHandlerOneMutationArchitectureTests`,
`InlineDmlArchitectureTests`, `ApplicationPublicSurfaceTests`, and `PersistencePublicSurfaceTests`
all exist (stage 4). Yet the plan's own status block and the plans index both still say `Proposed`.

`docs/plans/README.md`'s review checklist is explicit that a `Proposed` status block invalidates the
plan as gate evidence regardless of what the code shows. Right now the drift runs the other way —
done work that reads as not done — which is the exact failure mode the checklist exists to prevent.

**Remediation:**

- Walk the 2026-07-23 plan's §4 completion criteria against the code and test suite. For each
  criterion, record the fulfilling commit/test.
- If all criteria hold, set the plan's status block to `Implemented` with that evidence, and update
  the index row. If any criterion does not hold, record precisely which, and leave the status
  honest.
- While there, confirm `jobtrack_impl_plan.md`'s "Proposed 4 (active)" is still the intended
  reading for the phase tracker.

### 2.2 Full-table hierarchy loads are the read-path scaling ceiling

| | |
|---|---|
| **Severity** | **Medium** today; grows linearly with total installation size |
| **Category** | Scalability |
| **Files** | `PostgreSqlAwaitingProgressQueryPort.cs`, `PostgreSqlReadinessQueryPort.cs`, `PostgreSqlCostQueryPort.cs` (`CostQueryAssembly.LoadNodesByIdAsync`), and their SQLite twins |
| **Status** | **Fixed 2026-07-25.** Cost, Awaiting Progress, and single-node Readiness no longer materialize the installation-wide hierarchy; the remaining cost session scope is bounded to its cost window. Full before/after measurements: `docs/traceability/performance-budgets.md`'s "Full-table hierarchy load curve" section. |

Three read families materialize the **entire** `job_node` table (plus all `leaf_work` rows and all
prerequisite edges) into memory on every request, then hand the graph to the pure domain
calculators:

- Awaiting Progress (every page view of `/Jobs/AwaitingProgress`),
- Readiness,
- every cost read — where `maxHierarchyNodes` caps the *requested subtree* only; the whole-table
  load happens before the cap is evaluated, so a single-leaf cost query still pays O(total nodes).
  Cost reads additionally load each contributing worker's **database-wide** session history (ADR
  0017's elevated read scope for a correct concurrency divisor), and `CostSegmentPartitioner` is
  documented O(P²) in sessions-per-worker.

This is a *deliberate* correctness-first design: ADR 0017 requires the elevated scope, ADR 0014
assumes a single server, and the 2026-07-09 overlapping-cost scale plan built real measurements
against the 150 ms leaf / 2 s branch budgets. The finding is not that the design is wrong today —
it is that the ceiling has no tripwire. Nothing tells an operator the installation has grown past
the shape the budgets were measured at, and per-request cost degrades for **every** user as
**total** nodes grow, not as *their* data grows.

**Remediation (in order; stop at the point the budgets say is enough):**

1. **Tripwire first — done.** `FullTableHierarchyLoadPerformanceTests` measures Awaiting Progress and
   a single-leaf cost read at broad-tree (10,002 nodes) and combined-production-tree (193,570 nodes)
   scale; results recorded in `performance-budgets.md`.
2. **Scope the node load — done.** `CostQueryAssembly.LoadSubtreeAsync` now loads only the requested
   root(s)' own subtree via a new set-based recursive query (PostgreSQL: `job_node_subtrees`/
   `job_node_ancestor_chains` stored functions added to schema version 0013; SQLite: a parameterized
   recursive CTE mirroring `SqliteControlledLeafQuery`'s pattern), then `ExtendAncestryAsync` fetches
   exactly the ancestor chains ADR 0017's elevated scope still needs — each requested root's own path
   above itself (a rate override can be declared there; ADR 0040's owner carve-out walk needs it too)
   and any out-of-subtree contributing session's own path to the root (`RateResolver`'s
   nearest-ancestor walk). **Correction found while implementing this step:** the original write-up
   assumed the *bounded-depth* Browse subtree query (ADR 0039, capped for pagination) could be reused
   directly — it can't, since a cost read needs the *whole* subtree up to `maxHierarchyNodes`, not a
   depth-capped page. The new `job_node_subtrees` function is unbounded-depth like
   `JobNodeHierarchyQueries`'s other recursive queries, relying on the DB's cycle-free invariant for
   termination, same as its siblings.
3. **Bound the session history — turned out to already be satisfied, no code change.** Investigation
   before implementing step 2 found `LoadWorkerSessionsAsync`'s `worker_overlapping_sessions` call
   already bounded by the query window (`[bounds.Start, bounds.End]`, schema version 0018's
   `session_range && tstzrange(...)` predicate) rather than a worker's unbounded history — the
   "database-wide" language in ADR 0017/the 2026-07-09 scale plan refers to scope *across leaves*
   (a worker's sessions anywhere they touched, not just the requested subtree), not scope *across
   time*. This is exactly what a correct concurrency divisor requires; no narrowing was needed or
   made.
4. **Scope the Awaiting Progress load — done 2026-07-25.** `PostgreSqlAwaitingProgressQueryPort`/
   `SqliteAwaitingProgressQueryPort` no longer load the whole `job_node`/`leaf_work`/`job_prerequisite`
   tables. The narrowed load is: (a) every currently-unfinished leaf (childless, not archived, no
   `leaf_work` or a non-terminal achievement) via a plain EF LINQ query (EF-first — no new stored
   function needed for this part); (b) each candidate's own ancestor chain to the true root, reusing
   `job_node_ancestor_chains` on PostgreSQL and a parameterized recursive CTE on SQLite (same
   elevated-scope shape as ADR 0017's cost-read narrowing); (c) the prerequisite edges reachable from
   that scope; (d) for a required job *outside* that scope (a branch or an already-finished leaf this
   narrowed load never otherwise materializes), its recursive achievement resolved once through the
   existing `node_succeeded` stored function (PostgreSQL) or `JobNodeHierarchyQueries
   .IsSubtreeAchievedSqliteAsync` (SQLite) rather than materializing that subtree in memory —
   `AchievementCalculator.IsAchieved` only ever reads a required job's own `ChildIds`/`LeafAchievement`
   entry, so representing it as a childless node carrying the already-resolved answer is exact
   regardless of its true structure. **Correction found while implementing this step:** the original
   write-up assumed Awaiting Progress "genuinely needs the whole tree for its fold" — false. Its own
   roll-up (`AwaitingProgressCalculator`) never aggregates branch achievement itself; only
   `ReadinessCalculator`'s required-job lookup (via `AchievementCalculator`) needs a required job's
   recursive achievement, and that is exactly what the DB-side `node_succeeded`/
   `IsSubtreeAchievedSqliteAsync` mechanisms already compute correctly without materializing the
   subtree. A second, sharper correction found mid-implementation: a naive narrowed representation
   (any excluded node given `ChildIds = []`) is indistinguishable from a genuine unfinished leaf to
   `AwaitingProgressCalculator`'s own re-scan of the returned node graph (it re-derives its candidate
   set by scanning for "childless, non-terminal achievement" rather than trusting an external list) —
   every non-candidate placeholder must carry a terminal achievement sentinel
   (`AwaitingProgressQueryAssembly.NotACandidateSentinel`, `Achievement.Cancelled`, chosen because any
   terminal value works equally for `AchievementCalculator`'s Success/not-Success distinction) or it
   silently reappears as a spurious result.

**TDD evidence (step 2):** `CostQueryPortContractTestsBase.
GetCostInputsAsync_excludes_nodes_outside_the_requested_subtree_while_still_resolving_a_true_root_override`
(new, both providers) proves the narrowing (a 30-node decoy subtree never appears in `NodesById`) and
correctness (a rate override on the true root, above the requested leaf's own subtree, still
resolves) in one test. Every pre-existing `CostQueryPortContractTestsBase`/`OverlappingCostScale*`
test passes unchanged on both providers, plus the full PostgreSQL (418) and SQLite (416) persistence
suites and all 46 architecture tests. Measured result: cost-read node count at combined-production-
tree scale dropped from 193,570 to 7; latency from 360 ms to ~101 ms.

### 2.3 Per-request security-stamp validation multiplies every page's query count

| | |
|---|---|
| **Severity** | **Low–Medium** |
| **Category** | Scalability / deliberate trade-off to document |
| **Files** | `src/JobTrack.Web/Program.cs` (`SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero`) |

Every authenticated request re-validates the security stamp against the identity store — a DB
round-trip per request, before the request's own reads. Spec §7.1's instant-revocation requirement
motivates it and it should stay; but it is invisible load that compounds finding 2.2. Record it in
the performance-budgets doc as a fixed per-request tax, and if the tripwire in 2.2 fires, consider
a short validation interval (e.g. 5–15 s) as a named, ADR-documented relaxation rather than an
emergency hack.

### 2.4 Single-instance state is scattered, not fenced

| | |
|---|---|
| **Severity** | **Low** (by design under ADR 0014) |
| **Category** | Scale-out readiness |
| **Files** | `Program.cs` (`AddDistributedMemoryCache` session), `LoginAttemptRateLimiter.cs`, `PendingPatDeliveryStore.cs`, built-in `RateLimiter` partitions |

Four separate in-process stores assume exactly one web instance: the session-backed filter memory,
the login rate limiter, the pending-PAT delivery store, and the API rate-limit partitions. ADR 0014
makes single-server a real decision, so none of this is a defect — but the assumption is embodied
in four unrelated places with no single marker. If a second instance is ever stood up, the failure
is silent (rate limits double, PAT delivery misses, filters flap) rather than fail-closed.

**Remediation:** one short section in `docs/operations/` (or an addendum to ADR 0014) enumerating
every in-process store that breaks under multi-instance, referenced from each of the four call
sites with a one-line comment. No code change; the point is that scale-out starts from a checklist,
not an archaeology dig.

### 2.5 `JobTrackApi.cs` is a 2,600-line single file — RESOLVED 2026-07-25

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | Maintainability |
| **Files** | `src/JobTrack.Web/JobTrackApi.cs` (2,634 lines), also `Pages/Jobs/Browse.cshtml.cs` (826), `Pages/Jobs/Work.cshtml.cs` (690) |
| **Status** | **Done.** Split into `JobTrackApi.cs` (composition root: `MapJobTrackApi`, auth/error handling, shared response envelope types) plus `JobTrackApi.Rates.cs`, `.Jobs.cs`, `.Sessions.cs`, `.Cost.cs`, `.Schedules.cs`, `.Requests.cs`. |

Every external API endpoint — rates, jobs, sessions, prerequisites, achievement, cost, schedule,
requests — lived in one static class. Each handler is individually clean and thin (translate,
call `IJobTrackClient`, map exceptions), so this was congestion, not rot; but the file was the
place every API change collided, and "one public type per file" is house style everywhere else.

**Remediation (done):** split by resource into `partial class JobTrackApi` files
(`JobTrackApi.Rates.cs`, `JobTrackApi.Sessions.cs`, …) keeping the single route-group map method
(`MapJobTrackApi`) as the composition root in `JobTrackApi.cs`, alongside `AddJobTrackApi`,
`ExecuteAsync`'s exception-to-problem-details mapping, `HandleRedirectAsync`, the antiforgery/
telemetry endpoint filters, and the shared `PagedResponse<T>`/`AntiforgeryTokenResponse` envelope
types every partial uses. Pure file move — no behavioural change: the full 460-test
`JobTrack.Web.IntegrationTests` suite (which exercises every endpoint) passes unchanged, and the
solution builds/formats clean. Did not touch `Browse.cshtml.cs`/`Work.cshtml.cs`, per the original
"only if a real change lands there" scoping.

### 2.6 Dual-provider port duplication is a standing tax — RESOLVED 2026-07-25

| | |
|---|---|
| **Severity** | **Low** (accepted cost; watch it) |
| **Category** | Maintainability |
| **Files** | `PostgreSqlJobNodeCommandPort.cs` (971) vs `SqliteJobNodeCommandPort.cs` (958); `PostgreSqlWorkSessionCommandPort.cs` (922) vs `SqliteWorkSessionCommandPort.cs` (940); `CostQueryAssembly` defined separately in both cost ports |
| **Status** | **Investigated and closed as scoped.** `CostQueryAssembly`'s one genuinely provider-neutral, textually-identical member (`ClipEnd`) is lifted to `JobTrack.Persistence.Shared.SessionEndClipping`. Everything else in `CostQueryAssembly` (schedule expansion, rate resolution, `HierarchyNode`/`CostableSession`/`WorkerCostInputs` shaping) depends on `JobTrack.Domain`, which `JobTrack.Persistence.Shared` deliberately does not reference (impl plan §7.4) — extracting further would violate that project-layout constraint, exactly the "extraction fights the provider differences, stop and keep the duplication" case the remediation always anticipated. The command ports were not touched — their divergence (lock keys vs `BEGIN IMMEDIATE`, GiST vs trigger enforcement) is real, per the original write-up. |

The two providers' large command ports and the cost-assembly helpers are near-twins. The shared
contract-test bases make divergence *detectable*, which is why this is Low rather than Medium —
but every fix is written twice, and `CostQueryAssembly` in particular (pure EF LINQ over shared
entities plus in-memory graph shaping) looks provider-neutral.

**Remediation (done):** investigated lifting `CostQueryAssembly`'s provider-neutral members into
`JobTrack.Persistence.Shared` (internal, per ADR 0049's surface discipline). Only `ClipEnd` — two
lines, dependent solely on NodaTime — qualified; it is now `SessionEndClipping.ClipEnd`, called from
both providers, with the duplicate private methods removed. The rest of `CostQueryAssembly` (session/
schedule/rate loading and the `HierarchyNode` graph shaping) uses `JobTrack.Domain` types throughout
(`WorkInterval`, `ScheduleExpander`, `RateResolver`, `NodeRateOverride`, …), and `JobTrack.Persistence.Shared`
is scoped to reference only `JobTrack.Abstractions` — moving any of it there would require either
widening that scope (a bigger, unreviewed architectural change well beyond this finding) or duplicating
the Domain-dependent logic back into Shared under a different name, which fixes nothing. Per the
remediation's own stop condition, kept as accepted duplication. For the command ports, did **not**
force a shared base class — their divergence is real (lock keys vs `BEGIN IMMEDIATE`, GiST vs trigger
enforcement); no textually-identical extractable helper was found there either.

### 2.7 `JobTrack.Application` is a 200-file flat namespace — RESOLVED 2026-07-24

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | Maintainability / discoverability |
| **Status** | **Decided: keep flat, no restructuring.** ADR 0026 (M6 library gate) is already `Accepted`, which per `CLAUDE.md`'s public API discipline rule means `JobTrack.Application`'s namespace is **already** a compatibility commitment, not a future one. Moving any type into a sub-namespace now (`Application.Requests`, `Application.Sessions`, …) is a breaking change against an already-passed gate, not a pre-freeze cleanup opportunity. The window this finding worried about already closed. No code change; this note is the record so the question does not get re-raised as if it were still open. |

Request/result records, command/query services, and codecs all sit in one flat folder (only
`Ports/` is nested). The one-type-per-file rule is followed, which is what makes the flatness
visible.

## 3. Implementation order

### Stage 1 — Truth in documentation (no code)
- 2.1: verify and close the 2026-07-23 plan's status; fix the plans index. **Done.**
- 2.4: single-instance store inventory in operations docs. **Done.**
- 2.7: decide flat-vs-grouped `Application` namespace and record the decision. **Done — flat is
  final.**

### Stage 2 — Scaling tripwire

**Done.** `FullTableHierarchyLoadPerformanceTests` measures `GetAwaitingProgressInputsAsync` and a
single-leaf `GetCostInputsAsync` at broad-tree (10,002 nodes) and combined-production-tree (193,570
nodes) scale; results and the extrapolated trigger point are recorded in
`docs/traceability/performance-budgets.md`'s "Full-table hierarchy load curve" section, along with
the per-request security-stamp tax note (2.3). Headline result: at the project's own
"combined production tree" reference scale, a single-leaf zero-session cost read already costs
360 ms and an Awaiting Progress view 783 ms from the whole-table load alone — the trigger for Stage
3 is not hypothetical, it is close to already being crossed.

### Stage 3 — Read-scope narrowing (only as budgets demand)

**Done — closed 2026-07-25, all four steps.** See §2.2's own status line and remediation write-up
above for full detail:

**Follow-up 2026-07-25:** Awaiting Progress's multi-statement narrowed read now runs inside one
repeatable-read transaction on both providers. This prevents a structural move or prerequisite edit
from combining candidate facts from one database state with ancestor/edge facts from another.

**Follow-up 2026-07-25:** Readiness is no longer a whole-table read. Both providers now materialize
the checked node's ancestor chain, prerequisites declared there, and only the required-job subtrees
needed for recursive achievement derivation.

- Step 2 (cost read): `CostQueryAssembly.LoadSubtreeAsync`/`ExtendAncestryAsync` replace the
  whole-table load on both providers, new PostgreSQL stored functions (`job_node_subtrees`,
  `job_node_ancestor_chains`, schema version 0013) and a new SQLite parameterized recursive query,
  one new dual-provider contract test proving both the narrowing and correctness.
- Step 3: needed no change (already correctly bounded).
- Step 4 (Awaiting Progress): `PostgreSqlAwaitingProgressQueryPort`/`SqliteAwaitingProgressQueryPort`
  narrowed to unfinished leaves + their ancestor chains + required-job achievement resolved through
  the existing `node_succeeded`/`IsSubtreeAchievedSqliteAsync` mechanisms, per §2.2 step 4's own
  write-up (including the two corrections found while implementing it) — this was **not** deferred:
  the plan's earlier "structurally different, larger mechanism… genuinely needs to fold over the
  entire tree" premise turned out to be false once actually investigated, the same kind of
  correction step 2 already surfaced once. One new dual-provider contract test proves the narrowing
  (a 30-node finished decoy subtree never appears in `NodesById`) and a cross-branch, non-candidate
  required job's achievement still resolves correctly.

Full persistence/architecture suites green throughout (PostgreSQL 419, SQLite 417, 46 architecture
tests, Web integration 25).

### Stage 4 — Mechanical maintainability

**Done 2026-07-25, both items.**
- 2.5 partial-class split of `JobTrackApi`.
- 2.6 `CostQueryAssembly`'s one qualifying member (`ClipEnd`) lifted to
  `JobTrack.Persistence.Shared.SessionEndClipping`; the rest investigated and kept duplicated per the
  remediation's own stop condition (see §2.6's status line).

## 4. Completion criteria

1. No plan in `docs/plans/README.md` has a status block contradicted by the code.
2. `performance-budgets.md` states the measured node/session counts at which each full-table read
   path leaves budget, and the performance suite asserts inside that envelope.
3. **Done.** Cost reads no longer load `job_node` rows outside the requested subtree + ancestor
   chain, and Awaiting Progress no longer loads `job_node` rows outside currently-unfinished leaves +
   their ancestor chains.
4. The multi-instance breakage inventory exists and is referenced from each in-process store.
5. **Done.** `JobTrackApi` endpoints live in per-resource partial files; behaviour unchanged (full
   460-test `JobTrack.Web.IntegrationTests` suite passes unmodified).
