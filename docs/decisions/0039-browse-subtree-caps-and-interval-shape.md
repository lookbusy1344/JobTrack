# ADR 0039: Browse multi-level subtree — depth/breadth caps, interval shape, cost roll-up, filter pruning

**Status:** Accepted
**Closes:** `docs/plans/2026-07-15-browse-multi-level-subtree-plan.md` §3 (decisions 1-5).

## Context

The Browse multi-level subtree plan renders a bounded nested-set subtree — multi-level tree, cost
roll-up, and an interval/span column — under the current node. Per the plan's §3 and `CLAUDE.md`'s
decision-precedence rule, five product-semantic points had to be settled before Stage 2 (the
persistence query) could be written: depth cap, breadth cap, the public interval shape, roll-up cost
semantics, and archive/ownership filter pruning.

## Decision

1. **Depth cap.** Default render depth is the current node **+3 levels**; a caller may request more,
   up to a **hard cap of +5 levels**. The hard cap is enforced at the query/contract level, not just
   the default UI behaviour — a request above it is rejected, not silently clamped.

2. **Breadth cap — asymmetric by level.**
   - The subtree root's **immediate children (level 1)** are **never capped** — every direct child of
     the node being browsed renders, regardless of count.
   - For **every node whose children are being expanded to a further level** (this includes level-1
     children being expanded to level 2, and every deeper parent thereafter), only the **first 25
     children** (nested-set/`lft` order) have their own descendants fetched and rendered. Children
     26+ of that parent still render as a row (so the parent's full child count / structure is not
     hidden) but do **not** recurse further — no fetch below them, even if they themselves have
     descendants and even if the depth cap would otherwise allow it.
   - This is not a "+N more" truncation of level 1 — level 1 is exhaustive. It is a recursion gate:
     "does this specific child, beyond the 25th, get expanded downward." A `hasUnexpandedChildren`
     (or equivalent) flag on rows 26+ signals that a drill-in (re-root Browse at that node) will show
     more.

3. **Public interval/span shape.** Never publish raw `(lft, rgt)`. The subtree query and every layer
   above it expose **ordinal `subtreeLft`/`subtreeRgt`**, integers rebased to 0 at the requested
   subtree root — not a `0..1` fraction. Exact integer containment comparisons, no floating-point
   rounding in the contract; the web layer computes any CSS-relative fraction itself from the ordinal
   pair at render time.

4. **Roll-up cost semantics.** Every branch row carries its own rolled-up subtotal (sum of that
   branch's leaf costs) **and** the root read-out carries the subtree total. Each is boundary-rounded
   once (midpoint-to-even, ADR 0002's largest-remainder reconciliation applied at that read-out) —
   never accumulated from already-rounded intermediate sums.

5. **Archive/ownership filter pruning — structural pass-through.** `ArchiveFilter`/`OwnershipFilter`
   evaluate each leaf/branch individually for match/highlight purposes, but any ancestor of a
   matching descendant still renders as plain structure so the tree stays connected. Filters never
   shatter the tree into disconnected fragments by dropping a non-matching ancestor of a match.

## Rationale

- Level-1-exhaustive / 25-per-parent-thereafter mirrors how a user actually explores: the first
  screen of a node's direct children is the primary navigation surface and must not hide siblings
  behind a cap, while every level below that is where an unbounded fan-out becomes a real query/page
  cost — capping recursion there (not existence) keeps the query to one bounded range fetch with a
  predictable worst case (25^depth, not the true branching factor), without pretending the tree is
  smaller than it is (rows 26+ still render, just unexpanded).
- Ordinal rebased integers avoid publishing the internal nested-set encoding while staying exact;
  fractions would bake a derived, potentially-lossy float into a compatibility commitment for no
  benefit the web layer cannot compute itself.
- Root total is the summary metric; per-branch subtotal is what makes each branch's roll-up
  legible without re-deriving it client-side. Rounding once at the read-out (not per intermediate
  sum) is a straight application of ADR 0002 and prevents inconsistent branch/root arithmetic.
- Structural pass-through keeps Browse's existing "filters narrow, don't disconnect" behaviour (the
  single-level page already never hides an ancestor of a shown child) consistent in the multi-level
  case.

## Correction to the plan's premise (found during Stage 2 implementation)

The plan's §1 ("Current state") describes persisted `(lft, rgt)` nested-set columns already present in
`job_node` and used by `JobNodeHierarchyQueries.GetSubtreeAchievementsAsync`/`GetRequesterSubtreeAsync`
and `SqliteCostQueryPort.GetSubtreeIds`. This is factually wrong: `job_node` is a plain adjacency-list
table (`parent_id` only, both providers); those functions are `WITH RECURSIVE` CTEs (the hierarchy
queries) or an in-memory DFS over an already-loaded dictionary (the cost port), not `BETWEEN lft AND
rgt` range scans. No `lft`/`rgt`/`depth` property or column exists anywhere in `JobNodeEntity` or
`JobTrackModelConfiguration`.

**Decision:** do not add persisted `lft`/`rgt`/`depth` columns or a renumbering maintenance mechanism.
Stage 2 extends the existing `WITH RECURSIVE` adjacency-list pattern with a `depth` guard (bounded by
§1's cap) and the asymmetric breadth rule (§2), and computes `subtreeLft`/`subtreeRgt` as pre-
order/post-order ordinal counters **at read time** over the fetched, already-bounded row set — never
persisted, never renumbered on write. This keeps decision 3 (ordinal, rebased-to-root, integer span)
exactly as decided; only the computation mechanism changes, from "stored column range-scanned" to
"derived at query time from the adjacency list."

Consequence for the Stage 2 concurrency test: it asserts a coherent snapshot of `parent_id` edges
under a concurrent move/decompose (no row observing a stale parent alongside a fresh one), not "no
torn `(lft, rgt)`" — there is no stored `(lft, rgt)` to tear.

## Consequences

- **Contract shape:** `JobSubtreeResult` node rows carry `depth`, `subtreeLft`, `subtreeRgt`,
  `branchSubtotal` (nullable/absent on leaves per house style), `hasUnexpandedChildren`, alongside
  the existing id/parentId/derived-kind/owner/archived/`HasChildren`/`HasLeafWork` fields from the
  single-level result. The root read-out carries `rootTotal`.
- **Query shape:** one bounded nested-set range fetch (`lft BETWEEN root.lft AND root.rgt AND depth
  <= root.depth + maxDepth`), with the per-parent "first 25 by `lft` order get expanded" rule applied
  in the same pass (no per-node N+1) — Stage 2's shared contract test must assert exactly this: level
  1 unbounded, every deeper parent capped at 25 expanded children, `hasUnexpandedChildren` correct at
  the boundary.
- **API:** `GET /api/job-nodes/{id}/subtree?depth=` rejects `depth > 5` (external API plan's
  validation-error convention), defaults to `3`.
- **Web:** the "+N more" affordance from the plan's earlier wording is retracted at level 1 (never
  shown there) and reinterpreted at deeper levels as "row present, not expanded — drill in for more."
