# ADR 0040: Cost visibility grants an ownership carve-out alongside Administrator/CostViewer

**Status:** Accepted
**Closes:** a gap surfaced while building `docs/plans/2026-07-15-browse-multi-level-subtree-plan.md`
Stage 3 (subtree cost roll-up).

## Context

The Browse multi-level subtree plan's mockup shows a cost roll-up read-out as part of the ordinary
tree view every employee can browse. `IJobBrowseQueryPort`/`IJobQueries`' browsing methods carry no
ownership-based authorization gate at all — viewing job-tree structure is an unqualified baseline
capability for every role (spec §7.3). Cost data is the opposite: `Domain.Authorization.
CostAccessPolicy.CanView` gates `ICostQueries.GetCostDetailsAsync`/`GetHierarchyTotalsAsync` — the
mechanism the subtree roll-up would naturally reuse (`HierarchyDisplayReconciler`'s already-correct
per-level ADR 0002 rounding) — to `Administrator`/`CostViewer` only. Wiring the roll-up straight
through `ICostQueries` as-is would mean a job manager or worker who owns the very node they are
browsing cannot see its cost total, while a `CostViewer` with no connection to the node can.

## Decision

`CostAccessPolicy.CanView` gains a second input: whether the actor owns the queried node or any of
its ancestors (the same "controls the node" fact `JobNodeAccessPolicy.CanManage`,
`WorkSessionAccessPolicy.CanManage`, and `ScheduleAccessPolicy.CanManage` already take, computed via
`JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync`). An actor may view cost details/hierarchy
totals/subtree roll-ups if they hold `Administrator` or `CostViewer`, **or** own the queried node or
one of its ancestors:

```csharp
public static bool CanView(IReadOnlyCollection<EmployeeRole> actorRoles, bool ownsNodeOrAncestor) =>
    actorRoles.Contains(EmployeeRole.Administrator) || actorRoles.Contains(EmployeeRole.CostViewer)
    || ownsNodeOrAncestor;
```

This is a global change to the policy, not a subtree-roll-up-only carve-out: `/Jobs/CostReport`
(`ICostQueries.GetCostDetailsAsync`/`GetHierarchyTotalsAsync`) and the new Browse subtree roll-up
share one authorization rule, consistent with every other node-scoped policy in the codebase already
taking an ownership fact alongside roles.

`ICostQueryPort` gains `GetAncestorOwnerIdsAsync(JobNodeId nodeId, CancellationToken)`, implemented
by both providers as a thin delegation to the existing shared
`JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync` — no new SQL, reusing the exact mechanism the
command-port authorization checks already use.

## Rationale

- Matches the existing shape of every other node-scoped access policy in the codebase
  (`JobNodeAccessPolicy`, `WorkSessionAccessPolicy`, `ScheduleAccessPolicy` — role-or-ownership, never
  role-only) rather than leaving `CostAccessPolicy` as the one outlier.
- A node owner already has full command authority over their own subtree (edit, move, decompose,
  archive, delete); denying them visibility into what that subtree costs while a same-organisation
  `CostViewer` with no relationship to the node can see it is an inconsistent trust boundary, not a
  deliberate one — nothing in spec §7.3's role table says ownership is cost-blind.
- A global change (not a roll-up-only carve-out) keeps one authorization rule for cost visibility
  instead of two divergent ones a future reader would have to reconcile.

## Consequences

- **Breaking API change:** `CostAccessPolicy.CanView(IReadOnlyCollection<EmployeeRole>)` →
  `CanView(IReadOnlyCollection<EmployeeRole>, bool ownsNodeOrAncestor)`. `CostQueries.CalculateAsync`
  now loads ancestor owner ids before the authorization check, mirroring the command-port pattern.
- **`ICostQueryPort`:** new `GetAncestorOwnerIdsAsync` member on both providers.
- **`/Jobs/CostReport`:** a node owner who previously got `AuthorizationDeniedException` viewing their
  own subtree's cost now succeeds — a behavioural widening, not a narrowing; no existing caller loses
  access.
- **Docs:** `CostAccessPolicy`'s doc comment and any spec §7.3 role-table cross-reference describing
  cost visibility as `Administrator`/`CostViewer`-only need the ownership carve-out noted.
