# ADR 0002: Hierarchy-display penny reconciliation

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 6 (reconciliation half), §5.5 exit blocker

## Decision

Displayed parent totals must exactly equal the sum of displayed child totals at every level of the hierarchy, for every report that shows a hierarchy simultaneously (e.g. a job-detail page showing a node's cost next to its children's costs).

Algorithm, applied independently at each parent/children grouping being displayed together:

1. Compute each child's exact (unrounded) monetary contribution as already defined by the cost engine (§7.2 item 9: single rounded division per constant-rate segment, summed per child — the *segment* rounding is unaffected by this ADR).
2. Round every child's exact total to the nearest penny using midpoint-to-even (banker's rounding), producing a naive displayed total per child.
3. Compute the residual: `exact parent total (rounded to the penny with the same rule) − sum of naive child pennies`.
4. If the residual is zero, display the naive child pennies unchanged.
5. If the residual is non-zero, apply it entirely to the single child with the **largest absolute rounding error** (the child whose naive rounding moved furthest from its exact value in the direction that would cancel the residual). Ties break on the child with the lowest stable sort key (e.g. `JobNodeId`) for determinism.
6. The adjusted child display is exact-parent-consistent by construction; no other child is touched.

This is applied one level at a time (leaf-to-branch, branch-to-branch, branch-to-root) — reconciliation at one level does not skip levels above it.

## Consequences

- This is a *display* concern only. The underlying exact rational/decimal values (§5.1 item 6) are never mutated; reconciliation is computed at the reporting boundary and is disposable, matching the "derived values reproducible and disposable" principle (§14.1).
- The reconciliation function is pure and takes the already-computed exact child totals plus the exact parent total as input — no re-running of the cost engine.
- A property test must assert: sum of displayed child pennies == displayed parent pennies, for generated hierarchies of varying depth/breadth and generated cost values, including adversarial cases where several children round the same direction.
- A golden test fixes one hand-checked case showing the largest-remainder adjustment landing on a specific child, so a future refactor can't silently change which child absorbs the penny.
