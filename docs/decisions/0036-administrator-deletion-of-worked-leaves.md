# 0036: Administrators may physically delete a worked leaf; nobody may delete a subtree

## Status

Accepted.

## Context

Spec §4.6 (lines 171-175) states that completed jobs "shall never be physically deleted" because
"hierarchy and session history are inputs to dynamic costing," and that "any job with `LeafWork`, a
`WorkSession`, ... shall not be physically deleted." `IJobCommands.DeleteAsync` enforces this today
purely via `ON DELETE RESTRICT` foreign keys (`leaf_work.job_node_id → job_node`,
`work_session.leaf_work_id → leaf_work`, `job_node.parent_id → job_node`,
`job_prerequisite.{from,to}_id → job_node`): a single-row delete attempt fails, and any resulting
`DbUpdateException` is translated into `InvariantViolationException("job-node-not-deletable", ...)`
without distinguishing *why*.

Two gaps in that blanket rule surfaced from product discussion:

1. A leaf can have `LeafWork` attached (e.g. by mistake, via `AttachLeafWorkAsync`) with **zero**
   `WorkSession` rows — nothing has ever been worked or costed against it. Blocking deletion here
   serves no purpose the spec's rationale cares about: there is no session history and no cost
   figure that depends on it. This is a genuine gap in the existing rule, not a policy change.
2. An administrator may need to remove a leaf that genuinely does have logged `WorkSession` history
   — e.g. a job created and worked against in error, or one merged/duplicated by mistake. The spec's
   blanket "never" has no escape hatch for this, and `ICostQueries` computes cost live from current
   session data rather than storing report snapshots, so this is a real, deliberate exception to the
   spec's stated rationale, not merely closing a gap — it is accepted here as the cost of enabling a
   correction the business needs, restricted to the one role trusted to make that call.

Cascading deletion of a subtree is explicitly out of scope and not weakened by this decision:
deleting a node with children remains unconditionally rejected regardless of role, matching the
spec's prohibition on "silent cascading that changes workflow semantics" (line 224). The same
applies to prerequisite edges: a node with any `job_prerequisite` edge (either direction) must have
that edge explicitly removed via the existing `RemovePrerequisiteAsync`/Prerequisites page first —
deletion never silently drops a prerequisite edge. The permanent root remains undeletable
unconditionally (ADR 0015).

## Decision

`DeleteAsync` gains explicit, ordered pre-checks — replacing the previous reliance on a single
generic `DbUpdateException` catch — so each rejection reason has its own `ConstraintId`:

1. `node.ParentId is null` → `"job-node-is-root-cannot-delete"` (the root guard trigger remains the
   database-level backstop).
2. The node has any child → `"job-node-has-children-cannot-delete"`. Never cascades; the caller must
   delete/move children first.
3. The node has any `job_prerequisite` edge (required-by or requires) →
   `"job-node-has-prerequisites-cannot-delete"`. The caller must remove the edge(s) first.
4. The node has `LeafWork` attached:
   - Zero `WorkSession` rows for it → deletion proceeds, cascading the `LeafWork` row's removal in
     the same transaction as the `job_node` row. No elevated role and no `Reason` are required —
     this is the "closes an existing gap" case from Context item 1.
   - One or more `WorkSession` rows exist → the actor must hold the `Administrator` role, checked by
     a new `Domain.Authorization.JobNodeDeletePolicy.CanForceDeleteWorkedLeaf`, and
     `DeleteJobNodeRequest.Reason` (new, required only on this path) must be non-empty. On success,
     the transaction cascades: delete every `WorkSession` row for the `LeafWork`, delete the
     `LeafWork` row, delete the `job_node` row. A non-administrator hitting this path gets
     `AuthorizationDeniedException` (an authorization failure, not a data invariant) rather than an
     `InvariantViolationException`.
5. Otherwise (bare leaf/branch, no dependents) → deletion proceeds as today.

Before the cascading delete in step 4's worked-leaf branch, the audit event captures a summary of
what is being destroyed (session count, total worked duration, achievement state, `LeafWork`
criteria, the node's description) as its `beforeData`, since — unlike every other audited mutation
in this codebase — the underlying rows will no longer exist for anyone to look up afterward.
`audit_event.entity_id` is deliberately not a foreign key (schema version 0012), so the audit row
survives referencing a now-deleted node id exactly as the append-only audit design already intends.

`DeleteJobNodeRequest.Reason` is `string?`, required (non-empty) only when the worked-leaf path is
taken; validated application-side with a clear message rather than left to a database `NOT NULL`
that can't express the conditional requirement.

## Consequences

- Deleting a worked leaf changes what `ICostQueries.GetCostDetailsAsync`/`GetHierarchyTotalsAsync`
  compute for its former ancestors on any future call, since cost is calculated live from current
  session rows, never snapshotted. This is an accepted, documented trade-off of giving
  administrators an explicit correction tool — not something the operation guards against.
- `JobTrack.Web`'s Browse page gains a "Delete" action, visible when the current node has no
  children (regardless of role); if the node also has worked sessions, submitting shows the
  worked-leaf confirmation requiring a reason, and a non-administrator's attempt is rejected with a
  clear message rather than a raw 403.
- The generic `"job-node-not-deletable"` constraint id from `JobNodeWriteExceptionTranslation`'s
  catch-all remains as the backstop for any *other* unanticipated FK violation (e.g. a future table
  this decision didn't consider), but is no longer the primary signal for the cases enumerated
  above.
