# ADR 0042: Individual leaf costs are redacted inside an owned subtree; branch roll-ups are not

**Status:** Accepted
**Refines:** ADR 0040 (cost visibility's ownership carve-out).

## Context

ADR 0040 gave `CostAccessPolicy.CanView` an ownership carve-out so an owner could see cost on the
node they own without holding `Administrator`/`CostViewer`. That check is deliberately coarse: it
asks "does the actor own this node or an ancestor?" once, for the node being queried.

`CostQueries.CalculateAsync` authorizes exactly once against the requested node and then returns
`DisplayedCosts` for the **entire subtree**, and `JobQueries.GetJobSubtreeAsync` fills Browse's
per-row Cost column straight from that dictionary. The consequence: **owning a branch exposed every
descendant leaf's individual cost**, including leaves owned by other people.

That is a rate disclosure, not merely a cost one. A leaf's individual cost divided by that leaf's
session hours — which every employee can now read (ADR 0041) — yields the worker's effective hourly
rate. Spec §7.3 reserves rate visibility to `RateManager`/`CostViewer`
(`RateAccessPolicy`/`CostAccessPolicy`), and is explicit that rate management is granted "without
granting ... cost visibility". Reconstructing a colleague's pay rate from a browse screen defeats
that.

A branch's cost does not carry the same exposure: it is an aggregate over descendants, so no single
worker's rate is recoverable from it.

## Decision

Cost visibility is decided in **two** steps, not one:

1. `CostAccessPolicy.CanView(roles, ownsNodeOrAncestor)` — unchanged (ADR 0040) — admits the actor
   to a subtree's costs at all.
2. `CostAccessPolicy.CanViewNodeCost(roles, nodeHasChildren, nodeOwnerUserId, actorId)` — new — then
   decides each **individual** node's cost within it:

```csharp
public static bool CanViewNodeCost(
    IReadOnlyCollection<EmployeeRole> actorRoles, bool nodeHasChildren, AppUserId? nodeOwnerUserId, AppUserId actorId) =>
    actorRoles.Contains(EmployeeRole.Administrator)
    || actorRoles.Contains(EmployeeRole.CostViewer)
    || nodeHasChildren                      // a branch roll-up is an aggregate
    || nodeOwnerUserId is null              // unassigned: nobody's rate to infer
    || nodeOwnerUserId == actorId;          // your own leaf
```

So, for an actor who is not `Administrator`/`CostViewer`:

| Node | Individual cost shown? |
|---|---|
| A branch (any owner) | Yes — aggregate, no individual rate recoverable |
| A leaf they own | Yes |
| An unassigned leaf | Yes — no worker whose rate could be inferred |
| A leaf owned by someone else | **No** |

`JobQueries` applies it wherever a per-node cost is attached to a browse read model:
`GetJobSubtreeAsync` (each row *and* `RootTotal`, so browsing straight to another worker's leaf does
not leak through the root total), `EnrichSummariesWithCostAsync`, and
`EnrichAwaitingProgressWithCostAsync` (leaves by construction, so it reduces to "your own or
unassigned").

The dedicated cost surfaces are unaffected: `/Jobs/CostReport` is gated by the `RateRead` policy, so
only rate/cost roles reach it, and for them `CanViewNodeCost` is a no-op.

## Consequences

- A redacted cost renders as an empty cell, exactly as an unavailable cost already did — cost stays
  an optional field on an otherwise universally browsable listing, never a whole-request denial
  (ADR 0039 decision 4).
- `JobQueries` resolves the actor's roles through `IEmployeeQueryPort.GetActorRolesAsync` for this
  filter. `IJobBrowseQueryPort` does not return roles alongside its rows the way the work-session and
  cost ports do, so this is one extra lookup rather than the usual load-roles-in-the-same-round-trip
  shape; a role accessor on the browse port would remove it if that cost ever matters. The lookup is
  deliberately tolerant — an actor whose roles cannot be resolved yields no roles, the most
  restrictive answer, rather than failing the listing.
- **Known limitation — differencing.** A branch roll-up whose subtree contains exactly one
  other-owned leaf equals that leaf's cost; more generally, an actor who can see all but one leaf in
  a branch can subtract. Closing that properly needs aggregate-suppression (k-anonymity style)
  machinery well beyond this decision. The rule here removes the direct read, not every inference.
- Test fakes: `IEmployeeQueryPort` and the browse port must now agree about an actor's roles, since
  in production both read one database. `JobQueriesTests` seeds the employee fake from the browse
  fake rather than leaving the actor unknown to one of them.
