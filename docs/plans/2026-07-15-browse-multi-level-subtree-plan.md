# Browse multi-level subtree rendering (tree view, cost roll-up, nested-set intervals)

**Date:** 2026-07-15
**Status:** Implemented. Stage 1 (decisions/ADR) closed by
[ADR 0039](../decisions/0039-browse-subtree-caps-and-interval-shape.md); Stage 2 (persistence:
bounded-depth subtree query, both providers, shared contract + concurrency tests); Stage 3
(`GetJobSubtreeRequest`/`JobSubtreeResult`, `IJobQueries.GetJobSubtreeAsync`, cost roll-up via
`ICostQueries`, [ADR 0040](../decisions/0040-cost-access-owner-carve-out.md)'s ownership carve-out);
Stage 4 (`GET /api/jobs/{nodeId}/subtree`, OpenAPI contract, first-party client proof); Stage 5
(Browse renders the bounded multi-level tree, axe AA green on both providers); Stage 6 (efficiency
guards: fixed 3-round-trip persistence query, one batched cost call, regardless of subtree width) —
all implemented and verified. Full solution suite (`dotnet test JobTrack.slnx`, every project, both
providers) green.
**Depends on:** derived node-kind (ADR 0035, `docs/plans/2026-07-12-derived-node-kind-plan.md`),
node ownership/pickup (ADR 0031/0032), the cost engine and its reporting-boundary rounding (ADR
0002), and the mandatory implementation order in `jobtrack_impl_plan.md` §1 (Database → reusable
library → external HTTP API → web). Layers on top of the existing single-level `/Jobs/Browse`.
**Web design language:** `docs/design-language.md` ("Console"); the visual target is the reviewed
Browse mockup — a connected tree with a slate structural spine, a root cost roll-up read-out, and a
shared-scale interval column (computed ordinals, not persisted `lft`/`rgt` — see §1 correction and
ADR 0039).

## 0. Phase A already landed (context, not part of this plan)

Separately from this plan, the display face and one structural primitive are already in `main`-line
work: the self-hosted **Mulish** display face (Avenir Next first in the stack, Mulish the pinned OFL
fallback — `src/JobTrack.Web/libman.json`, `site.css` `@font-face` + `--jt-font-display`), the
`--jt-slate-*` structural token ramp, and the `.jt-kind` Root/Branch/Leaf chip used on the existing
single-level Browse. Those are the parts that needed **no** new query surface. This plan is the rest:
everything that needs data the public query surface does not expose yet.

## 1. Current state

`/Jobs/Browse` is a **single-level drill-down**, not a tree:

- `BrowseModel.LoadAsync` fetches the current node (`GetJobNodeAsync`) plus its **direct children**
  (`GetJobChildrenAsync`) — one level. Navigation re-roots Browse at a clicked child; ancestors show
  as breadcrumbs.
- `JobNodeSummaryResult` (the child row) carries id, derived kind, owner, priority, archived,
  `HasChildren`, `HasLeafWork` — **no cost**, and **no interval/span**.
- Roll-up cost exists only on the separate `/Jobs/CostReport` page, via the cost query port.

**Correction (found during Stage 2 implementation, recorded in ADR 0039):** this plan originally
claimed persisted `(lft, rgt)` nested-set coordinates already existed in the schema. They do not —
`job_node` is a plain adjacency-list table (`parent_id` only, both providers).
`JobNodeHierarchyQueries.GetSubtreeAchievementsAsync`/`GetRequesterSubtreeAsync` are `WITH RECURSIVE`
CTEs over `parent_id`, and `SqliteCostQueryPort.GetSubtreeIds` is an in-memory DFS — neither is a
`BETWEEN lft AND rgt` range scan, and no `lft`/`rgt`/`depth` column exists anywhere. Stage 2 does
**not** add persisted nested-set columns or a renumbering mechanism; it extends the existing
`WITH RECURSIVE` pattern with a depth guard and computes the ordinal `subtreeLft`/`subtreeRgt` span
(ADR 0039 decision 3) **at read time** over the bounded, already-fetched row set.

So the three things the mockup is built around — **multi-level nesting, the cost roll-up read-out,
and the interval bars** — all require new data crossing DB → library → API before the web layer can
render them. Per §1's architectural constraint, none of it may be faked in Razor by reaching past
`IJobTrackClient`.

## 2. Goal

Render, in Browse, a **bounded** subtree rooted at the current node:

- multi-level connected tree (trunk/branch spine, kind chips, status pills, Avenir/Mulish headings);
- a root **cost roll-up read-out** (sum of the subtree's leaf costs, rounded only at the reporting
  boundary per ADR 0002);
- the **nested-set interval column** — each node's span drawn on one shared axis so a branch visibly
  encloses its descendants (the signature element).

Bounded on **both axes** for efficiency: a maximum render **depth**, and a **recursion** breadth cap
per parent (level-1 children always all shown; deeper recursion capped at the first 25 children per
parent — ADR 0039), with a "drill in" affordance (re-root Browse at a node) for anything the caps
leave unexpanded. The existing single-level actions (inline start/finish, pick up, edit, move,
decompose, readiness, dependencies) must be preserved — see §7.

## 3. Decisions closed (ADR 0039)

These were product-/contract-semantic and were settled before Stage 2, per the ADR-precedence rule
in `CLAUDE.md`. See [ADR 0039](../decisions/0039-browse-subtree-caps-and-interval-shape.md) for full
rationale; summary:

1. **Depth cap + default.** Current node **+3 levels** by default, hard cap **+5 levels** —
   contract-level guard, not just a UI default.
2. **Breadth cap — asymmetric by level.** Level-1 (immediate) children of the subtree root are
   **never capped** — all render. For every parent whose children are expanded to a further level
   (level 1 onward), only the **first 25 children** (by `lft` order) get their own descendants
   fetched/rendered; children 26+ still render as a row but do not recurse further —
   `hasUnexpandedChildren` marks them for drill-in. No "+N more" truncation at level 1.
3. **Public interval shape.** Ordinal `subtreeLft`/`subtreeRgt`, integers rebased to 0 at the
   requested subtree root — never raw `(lft, rgt)`, never a `0..1` fraction (exact, no
   floating-point rounding in the contract).
4. **Roll-up cost semantics.** Each branch row carries its own rolled-up subtotal **and** the root
   read-out carries the subtree total; each boundary-rounded once (ADR 0002 largest-remainder
   reconciliation applied at the read-out, not per intermediate sum).
5. **Archive/ownership filters in a subtree.** Structural pass-through: filters mark
   match/highlight on individual leaves/branches, but any ancestor of a matching descendant still
   renders as structure — never shatters the tree into disconnected fragments.

## 4. Mandatory implementation order (each stage TDD, DB slices in the §6 order)

Per `CLAUDE.md` TDD: failing test first, smallest correct implementation, refactor. Per the
database-slice order: shared contract test → PostgreSQL enforcement → SQLite enforcement →
provider-specific concurrency/race test.

### Stage 1 — Decisions & ADR(s) [DONE — ADR 0039]
§3 closed by ADR 0039 (interval-exposure shape is the load-bearing one). No normative spec change
required. No code.

### Stage 2 — Persistence: bounded-depth subtree query [DONE]
Per the §1 correction (ADR 0039): `job_node` is adjacency-list (`parent_id`), not nested-set. One
`WITH RECURSIVE` query (extending the existing `JobNodeHierarchyQueries` pattern), no per-node N+1,
both providers. The recursive step encodes the asymmetric breadth rule directly so the DB, not the
app layer, enforces the cap:

```
base:      SELECT id, parent_id, 0 AS depth, TRUE AS expand
           FROM job_node WHERE id = @rootId
recursive: SELECT c.id, c.parent_id, p.depth + 1, expand_next
           FROM job_node c JOIN subtree p ON c.parent_id = p.id
           WHERE p.depth < @maxDepth AND p.expand
           -- expand_next: TRUE when p.depth = 0 (level-1 parent — the *root* itself —
           -- so all its children always qualify), OR when c is among the first 25
           -- children of p ordered by c.id (rank via a correlated COUNT(*), not
           -- ROW_NUMBER() — SQLite rejects window functions inside a recursive CTE
           -- term); children beyond the 25th still appear in this result
           -- (p.expand admitted them) but get expand_next = FALSE, so they never spawn
           -- their own recursive row and hasUnexpandedChildren is expand_next = FALSE
           -- AND (a further child exists in job_node, checked once via HasChildren).
ORDER BY <pre-order via a materialized path or a recursive ordinal column>  -- render order
```

Returns per node: id, parentId, derived kind (ADR 0035 — derived, never a stored column), depth,
owner, archived, `HasChildren`/`HasLeafWork`, and `hasUnexpandedChildren`. `subtreeLft`/`subtreeRgt`
(§3.3) are **not** part of the SQL result — they are computed in the application/mapping layer (Stage
3) as pre-order/post-order ordinal counters over the already-fetched, already-bounded row set,
rebased to 0 at the query root; this avoids persisting or renumbering any interval column. The
roll-up cost (§3.4) is a second batched call into the existing cost query port over the same bounded
id set (mirror `GetActiveSessionsAsync`'s batched-by-id-list shape), not inlined into the recursive
query. Breadth rule (§3.2, ADR 0039): level-1 children of the subtree root are never capped (the root
itself always has `expand = TRUE` unconditionally); every parent whose children are expanded to a
further level only has its first 25 children (by `id`) recursed into — children 26+ still appear as
rows with `hasUnexpandedChildren = true` and no descendants fetched.
Tests: shared contract test (shape, order, depth bound, level-1-unbounded, 25-per-parent recursion
cap, `hasUnexpandedChildren` boundary, filter composition) → PostgreSQL → SQLite → a concurrency test
(subtree fetched while a concurrent move/decompose changes `parent_id` edges — assert a coherent
snapshot, no row observing a mix of pre- and post-move parentage; per ADR 0039's correction there is
no stored `(lft, rgt)` to tear).

### Stage 3 — Library: Abstractions + Application [DONE]
`GetJobSubtreeRequest` / `JobSubtreeResult` / `JobSubtreeNodeResult` land in `JobTrack.Application`
(matching the existing `GetJobChildrenRequest`/`JobNodeSummaryResult` convention — not
`JobTrack.Abstractions`, which holds only cross-cutting primitives like `JobNodeId`/`NodeKind`/`Money`).
`IJobQueries.GetJobSubtreeAsync` combines `IJobBrowseQueryPort.GetSubtreeAsync` (Stage 2) with
`ICostQueries.GetHierarchyTotalsAsync` (reusing its existing ADR 0002 reconciliation rather than
duplicating the cost engine) and computes `subtreeLft`/`subtreeRgt` at read time
(`JobSubtreeOrdinals`, a pure pre-order/post-order walk over the fetched rows). Cost visibility surfaced
a real authorization-model gap: `CostAccessPolicy` only granted `Administrator`/`CostViewer`, unlike
every other node-scoped policy's role-or-ownership shape — closed by
[ADR 0040](../decisions/0040-cost-access-owner-carve-out.md) (an owner may view their own subtree's
cost) rather than special-cased in this feature alone. An `AuthorizationDeniedException` from the cost
call is caught and translated to `RootTotal`/`Cost` = `null` — never a whole-request denial, since
structure browsing carries no ownership gate. `JobQueries` constructor gained an `ICostQueries`
dependency; both provider composition roots updated. Unit tests cover span computation and the
cost-omitted/cost-included paths (`JobQueriesTests`, fakes).

### Stage 4 — External HTTP API [DONE]
`GET /api/jobs/{nodeId}/subtree?depth=` — corrected from the original `/api/job-nodes/{id}/subtree`
wording to match every existing route's established `/api/jobs/{nodeId}/...` prefix and parameter
name (`JobTrackApi.cs`'s single minimal-API module); defaults to 3, 400s for `depth` outside
`[0, JobSubtreeLimits.HardMaxDepth]` via the existing `ArgumentOutOfRangeException`→400 conversion
(no bespoke Web-layer validation needed). `AnyEmployee`-gated as a whole; `rootTotal`/each node's
`cost` are individually `null` when the actor may not view cost (ADR 0040), never a 403 for the
endpoint. `JobSubtreeResponse`/`JobSubtreeNodeResponse` DTOs (never the internal `JobSubtreeResult`
type) plus `Map` overloads follow the file's existing pattern exactly. `OpenApiContractTests`'
`ExpectedContract` exact-route-set and explicit-bound-parameter assertions extended; new
`JobSubtreeApiTests` (structure-only 200, ownership-carve-out 200-with-cost, depth-cap 400,
nonexistent-root 404). `samples/JobTrack.ExternalApiClient` gained `GetJobSubtreeAsync` +
`JobSubtree`/`JobSubtreeNode` plain-JSON models (still zero `ProjectReference` to any `JobTrack.*`
library assembly) and a read-workflow exercise in `ExternalApiClientProofTests` (both providers).
`docs/api/external-http-api-reference.md` updated.

### Stage 5 — Web: the tree render [DONE]
Chose the **accessible table equivalent** the plan named as an alternative to `role="tree"`/`treeitem`
(native `<table>`/`<th scope="col">`/`<tr>`/`<td>` semantics, no custom ARIA tree roles) rather than a
hand-rolled tree widget — the existing single-level Browse table was already an axe-passing pattern,
and no `role="tree"` precedent existed anywhere in the codebase to build on. New named `site.css`
components layered over Bootstrap: `.jt-tree-indent` (depth-based indentation, paired with a
visually-hidden "Level N" label per row so depth is accessible, not just visual), `.jt-tree-span-track`/
`.jt-tree-span-fill` (the nested-set interval bar, `aria-hidden` since it's structural/decorative —
the row's own text already conveys containment). The cost roll-up read-out reuses `.jt-metrics`/
`.jt-metric` as-is. `Browse.cshtml` renders `Model.SubtreeDescendants` (pre-order via `SubtreeLft`,
computed in `BrowseModel` rather than inline Razor `@{ }` blocks, which hit a parser edge case nested
this deep) with a "+ more →" drill-in link on `HasUnexpandedChildren` rows. Every existing action (§7)
preserved per the proposed decision: inline start/finish/pick-up render on every visible leaf/unowned
row across the whole rendered subtree (not just level 1), batched into one active-session lookup
exactly as before; the heavier actions (edit/move/decompose/readiness/dependencies) stay on the
drilled-in current node only. One pre-existing integration test asserted the single-level behaviour
directly ("browsing root does not show grandchildren") — updated to assert the new depth-bound
behaviour instead (grandchildren within the default depth do render; a 4-levels-deep descendant does
not), since that was the single-level constraint this stage deliberately replaces.
**Axe re-scan passed clean** on both providers (`JobBrowseBrowserTests`, `JobNodeStructureBrowserTests`)
with no code changes needed for AA — the table-equivalent choice avoided introducing untested ARIA
surface.

### Stage 6 — Efficiency guards (cross-cutting, asserted) [DONE]
`CommandCountInterceptor` (new, `JobTrack.TestSupport`) attached via a test-only internal constructor
overload on both `PostgreSqlJobBrowseQueryPort`/`SqliteJobBrowseQueryPort` (no production-facing API
change — both classes are already `internal`). `GetSubtreeAsync_executes_a_fixed_number_of_round_trips_regardless_of_subtree_width`
(shared contract test, both providers) seeds `BreadthCap + 10` siblings and asserts exactly **3** SQL
round trips (root-existence check, the bounded recursive fetch, the shaped-detail fetch) — fixed
regardless of width, proving no N+1. `GetJobSubtreeAsync_batches_the_cost_roll_up_into_one_call_regardless_of_subtree_width`
(`JobQueriesTests`, using `FakeCostQueries`' new call-count tracking) proves the cost roll-up stays
one batched `ICostQueries` call as the subtree widens. Depth/breadth caps were already asserted by
Stage 2's contract tests (`_never_caps_the_root_s_immediate_children`,
`_caps_recursion_at_the_breadth_cap_for_non_root_parents`, both using a `BreadthCap + 5`-wide
synthetic fixture) — Stage 6 didn't need to re-derive those.

## 5. Efficiency requirements (the "limited to a certain depth" ask, made concrete)

- **Depth-bounded** render (§3.1) — the page never walks an unbounded subtree.
- **Breadth-bounded recursion** per parent (§3.2, ADR 0039) — level 1 always shows every child; a
  500-child branch below level 1 shows all 500 rows but only the first 25 recurse further, the rest
  marked `hasUnexpandedChildren` for drill-in.
- **One query, no N+1** — nested-set range fetch; cost/status batched. Asserted in Stage 6.
- Deeper/wider exploration is **drill-in** (re-root, reuse existing navigation), not one giant page.

## 6. Accessibility & design constraints

- WCAG AA is a build-failing gate (axe in `JobTrack.Web.EndToEndTests`). Slate is structure-only and
  non-text; text-bearing chips/read-out are AA-verified. Re-scan after any colour/structural change.
- Prefer an existing token/primitive over bespoke CSS; add named components with comments when new.
- Motion (spine draw / row reveal, if any) collapses under `prefers-reduced-motion`.
- No real PII in fixtures/screenshots (`CLAUDE.md`).

## 7. Preserving existing Browse behaviour (non-negotiable)

Today's Browse folds a lot into one page: inline session start/finish/backdate per leaf, pick-up,
edit/move/decompose, readiness, dependencies, leaf work-session panel, recently-visited history,
request context. The multi-level view must not regress these. Decide during Stage 5 whether inline
per-leaf actions render on every visible leaf row in the tree, or collapse to the drilled-in node
only (proposed: keep inline start/finish on visible leaf rows — recording work is the most common
action — and keep the heavier actions on the drilled-in/current node). This is a UX decision to
settle with evidence (the mockup shows actions on the current node; the tree adds many leaf rows).

## 8. Non-goals / deferred

- Client-side lazy expand/collapse (progressive fetch of deeper levels) — a later enhancement; the
  first cut is server-rendered bounded depth with drill-in.
- Virtualized rendering — unnecessary once depth+breadth are bounded.
- Changing the cost engine or its rounding — reuse ADR 0002 as-is.

## 9. Definition of done

Decisions closed with ADR(s) (§3); bounded-depth subtree query on both providers with the §6-order
tests; new library types reviewed against the FDG; HTTP API endpoint + OpenAPI + client-proof update;
Browse renders the bounded multi-level tree with roll-up + interval column, all existing actions
intact; efficiency guards asserted; axe AA green on both providers; `dotnet build -warnaserror` and
`dotnet format --verify-no-changes` clean. Update this plan's status block and the `docs/plans/README`
index when the work lands.
