# ADR 0061: Administrators may recursively delete or archive a whole subtree

**Status:** Accepted
**Supersedes:** ADR 0036's prohibition — "Cascading deletion of a subtree is explicitly out of scope
and not weakened by this decision: deleting a node with children remains unconditionally rejected
regardless of role", and its rule that "deletion never silently drops a prerequisite edge".

ADR 0036's *single-node* rules are otherwise untouched: `DeleteAsync` keeps rejecting a node with
children, and the permanent root stays undeletable under every operation defined here.

## Context

ADR 0036 gave administrators an escape hatch for one leaf worked in error. Operationally the
recurring need is larger: a whole branch of work — a cancelled project, a duplicated import, a
mis-parented decomposition — has to go, and clearing it one leaf at a time is impractical because
`DeleteAsync` refuses any node with children. The only route today is a manual leaves-upward walk,
which is slow, error-prone, and abandons partial state if interrupted.

The owner was shown ADR 0036's prohibition and the two costs below, and reaffirmed the requirement
for full recursive deletion including worked session history, restricted to administrators.

Two consequences are accepted deliberately, not mitigated:

1. **Historical cost figures move.** `ICostQueries.GetCostDetailsAsync`/`GetHierarchyTotalsAsync`
   compute from current `work_session` rows and never snapshot a report. Destroying a subtree's
   sessions retroactively changes what every surviving ancestor reports. ADR 0036 already accepted
   this for one leaf; this ADR accepts it at subtree scale, where the swing can be large.
2. **Spec §4.6's "never physically deleted" is now overridden for a bounded, audited, admin-only
   operation** rather than merely gapped. The audit event is the sole surviving record.

## Decision

Two new commands, both single ACID transactions committing once, both requiring
`EmployeeRole.Administrator` (`JobNodeDeletePolicy.CanDeleteSubtree`), both taking the subtree root's
optimistic-concurrency `Version`:

- `IJobCommands.DeleteSubtreeAsync` — physically destroys the subtree.
- `IJobCommands.ArchiveSubtreeAsync` — sets `archived_at` on every not-yet-archived node in the
  subtree. Non-destructive, offered as the alternative wherever deletion is offered.

A third, read-only member backs the confirmation screen:

- `IJobQueries.GetSubtreeDeletionImpactAsync` — the manifest of exactly what would be destroyed.

### The impact manifest

Computed by one unbounded recursive CTE over the adjacency list, following the existing
`JobNodeHierarchyQueries` pattern (termination relies on the DB-enforced cycle-free invariant,
schema version 0005 — not a depth cap). Deliberately *not* built on `GetBoundedSubtreeAsync`, whose
ADR 0039 depth (+5) and breadth (25) caps would silently under-report what deletion destroys.

It carries exact counts of nodes, leaves with `LeafWork`, `WorkSession` rows, total worked duration,
prerequisite edges crossing into the subtree from outside, and `job_request` rows; plus the node
rows themselves for display. `DeleteSubtreeAsync` recomputes the manifest inside its own transaction
rather than trusting anything the caller round-trips.

### What the cascade destroys

In dependency order, inside the one transaction: `work_session` → `leaf_work` →
`node_rate_override` → `job_request_note` → `job_request` → `job_prerequisite` → `job_node`
(deepest-first). `employee.home_node_id` is `ON DELETE SET NULL` (schema version 0004) and clears
itself; every other reference is `ON DELETE RESTRICT` and must be removed explicitly above.

**Prerequisite edges are dropped, not refused** — reversing ADR 0036 on this point, at the owner's
direction. Every edge with either endpoint inside the subtree is deleted, including edges arriving
from nodes *outside* it. A surviving external dependent therefore silently loses a prerequisite and
may become ready where it was blocked. The manifest counts these inbound external edges separately
and the confirmation screen names them, so the decision is informed rather than hidden; ADR 0051's
"blocked is a state, not an error" means the resulting readiness change is valid, not a corruption.

### What the cascade refuses

`request_holding_area.job_node_id` anchoring at any node in the subtree aborts the operation with
`InvariantViolationException("subtree-delete-holding-area-anchored")`, listing the areas. A holding
area is a *department's intake configuration*, not the subtree's own data: destroying it would break
future request routing for people with no connection to the deleted work. The administrator
re-anchors or deactivates it first. This is the one dependency the operation will not silently take
with it, and it is a narrower carve-out than ADR 0036's blanket prerequisite refusal.

The permanent root (`parent_id IS NULL`) is never deletable (ADR 0015); `ArchiveSubtreeAsync` may be
rooted anywhere, including the root.

### Reason and audit

`Reason` is **required and non-empty on every `DeleteSubtreeAsync` call**, not only when sessions
exist — unlike single-node `DeleteAsync`, where ADR 0036 makes it conditional. A subtree deletion is
destructive by construction and always warrants a recorded justification.

One `delete-subtree` audit event is written against the subtree root before any row is removed, with
the whole manifest as `beforeData`: per-node descriptions and achievements, session counts and total
worked duration, dropped prerequisite edges (both endpoints), and destroyed `job_request` ids. As in
ADR 0036, `audit_event.entity_id` is not a foreign key (schema version 0012), so the event outlives
the rows it describes. `ArchiveSubtreeAsync` writes one `archive-subtree` event with the affected
node ids.

### Active sessions

`DeleteSubtreeAsync` destroys sessions outright, so an open session is not an obstacle and is
included in the manifest's counts. `ArchiveSubtreeAsync` keeps the existing single-node guard: it
fails with `leaf-closure-active-sessions` if any leaf in the subtree has an active session, since an
archived leaf must not carry a running session.

## Consequences

- `JobTrack.Web` gains `/Jobs/DeleteSubtree`, an Administrator-only confirmation page reached from
  Browse, showing the manifest, a destructive-action warning, a required reason, and an "Archive
  instead" submit alongside the delete submit. Per ADR 0044 it names its node as a link back to
  Browse and returns there by PRG — to the deleted subtree's *parent* on deletion.
- The Browse "Delete subtree" action renders only for an administrator on a non-root node with
  children; single-leaf deletion keeps using `/Jobs/Delete` unchanged.
- Cost reports covering a deleted subtree's former ancestors change value with no in-band
  explanation beyond the audit event. Accepted per Context item 1.
- A large subtree deletion holds row locks across the whole cascade for the duration of one
  transaction; the operation is administrative and rare, and is not additionally rate-limited.
