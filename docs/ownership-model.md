# Job-node ownership, assignment, and work authorization

Status: **accepted**, closed by [ADR 0031](decisions/0031-job-node-ownership-unassigned-pool-and-pickup.md)
(ownership states, unassigned pool, pickup, ancestor cascade) and
[ADR 0032](decisions/0032-owner-gated-work-session-authorization.md) (owner-gated work-session
recording, `WorkSessionAccessPolicy` breaking change). Implementation is tracked by
[`plans/2026-07-11-job-node-ownership-and-work-authorization.md`](plans/2026-07-11-job-node-ownership-and-work-authorization.md).
This document is the normative narrative; the ADRs record *why*, the plan records *how*. A proposed
extension for requester/client intake and holding-area configuration is tracked by
[`plans/2026-07-11-client-requester-intake-plan.md`](plans/2026-07-11-client-requester-intake-plan.md)
and documented in §9 onward.

This file is directives on the ownership model. Entity field detail lives in
[`database-entities.md`](database-entities.md); role definitions in `jobtrack_spec_codex.md` §7.3.

## 1. Why this model exists

The legacy schema attached at most one work record to a node (`0..1`), so "who worked this node"
was unambiguous by construction. The current schema allows **many** `work_session` rows per
`leaf_work`, each carrying its own `worked_by_user_id` — so a leaf can accumulate time from several
people. That is desirable, but it removed the implicit single-worker guarantee and left work-session
authorization gated only on *self-vs-others* (`WorkSessionAccessPolicy.CanManage(roles, isOwnSession)`),
which means any authenticated Worker can record their own time against **any** node in the system,
regardless of who controls it. That is too open.

The replacement makes **node ownership** the single axis of control for both structure and work,
and reintroduces a controlled, explicit way for a worker to acquire a node to work on: the
**unassigned pool** and **pickup**.

This model deliberately separates **technical ownership** from **requester visibility**. The
`job_node.owner_user_id` column answers "who controls this work in the technical hierarchy?" It does
not answer "who asked for this work?" or "who is allowed to monitor it as a client." Requester
intake uses a separate request anchor (§9) so client users can submit and track jobs without being
made Workers and without inheriting operational tree access.

## 2. The core concept: direct ownership grants control, not evidence of work

Every `job_node` (root, internal branch, and leaf alike) carries `owner_user_id`, now **nullable**:

| `owner_user_id` | Meaning |
|---|---|
| a user id | **Directly owned.** This employee is the node's direct owner. |
| `NULL` | **Unassigned.** The node is in the public pool; any worker may pick it up. |

Direct ownership is one input into the computed **control** predicate (§3): a node's direct owner
controls that node, and so does the direct owner of any ancestor. Ownership is **not** evidence that
the owner performed any work; work performed is recorded solely by `work_session` rows and their
`worked_by_user_id`. A node directly owned by Alice may hold sessions worked by Alice, Bob, and
Carol — Alice, as a controller of the node, is one of the people permitted to enter them.

Ownership is **never inferred** from the posting user, the current worker, or any client-supplied
claim. It is set explicitly at creation, changed only by an explicit reassignment, and acquired from
the pool only by an explicit pickup.

### 2.1 The `NULL` state is deliberate and self-standing

`owner_user_id IS NULL` is a first-class, intended state meaning *"this job is unassigned and anyone
may pick it up."* It is **not** a data defect or a "missing owner" to be backfilled. Its pickup
availability does **not** depend on ancestors (see §4): a node is publicly claimable whenever its own
`owner_user_id` is `NULL`, even if an ancestor is owned.

The one exception: the **permanent root** node may never be unassigned. Its owner is set at
installation bootstrap and must always be non-null.

## 3. Ownership cascades down the tree (ancestor rule)

Directly owning a node grants control over its entire subtree. Formally, an actor **controls** a node
when they directly own that node **or** directly own any of its ancestors:

```
controls(actor, node) =
    exists n in { node } ∪ ancestors(node) : n.owner_user_id = actor
```

`NULL`-owned nodes on the path contribute nobody — they are skipped, not treated as "owned by the
current actor." So an unowned intermediate node does not break a higher owner's control, nor does it
grant control to a claimant of a *sibling*.

This is the "subfolder" model: directly own a folder and you control everything beneath it,
including still-unassigned descendants and descendants with their own direct owner. A descendant's
direct owner does not remove the ancestor owner's control; it adds another controller for that
descendant subtree. If you want to let someone self-manage part of your subtree, give them a child
node they own (§6).

## 4. Authorization predicates

All predicates default to **deny** and are evaluated against a fresh reload of the actor's current
roles and the node's current owner/ancestry — never from client-supplied claims (spec §7.3). The
role set is `{ Administrator, JobManager, Worker, RateManager, CostViewer, Auditor }`
(`EmployeeRole`); only the first three participate below.

Let `controls(actor, node)` be as defined in §3.

### 4.1 Structural management — create child, edit, move, archive, reassign owner

```
canManageStructure(actor, node) =
       actor has Administrator
    OR actor has JobManager
    OR (actor has Worker AND controls(actor, node))
```

Unchanged in shape from today's `JobNodeAccessPolicy.CanManage`; the only refinement is that
`controls` must skip `NULL` owners in the ancestor walk. For **child creation**, `node` is the
**parent** — you may add a child under any node you control.

For **moving/re-parenting**, `node` is the node being moved. An actor who can manage that node may
move it to any otherwise valid parent; they do **not** also need to control the destination parent.
This deliberately keeps the permission model simple for intake and evolving large jobs: an assigned
node owner, an owner of any ancestor of that node, JobManager, or Administrator can move the node
out of a holding area and into a more logical part of the hierarchy.

The destination is still validated by the hierarchy and workflow invariants:

- the permanent root cannot be moved;
- the new parent must exist;
- a node cannot be moved under itself or one of its descendants;
- the move must not leave a node with both `LeafWork` and children;
- prerequisite ancestor/descendant exclusions must still hold after the move; and
- the write remains optimistic-concurrency checked and audited.

### 4.2 Work recording — start / finish / correct a session

```
canRecordWork(actor, node) =
       actor has Administrator
    OR actor has JobManager
    OR (actor has Worker AND controls(actor, node))
```

This **replaces** the old `isOwnSession` rule. Consequences:

- The controlling owner (or Admin/JobManager) may record a session for **any** `worked_by_user_id` —
  entering time on behalf of other people is a first-class owner capability.
- A Worker who controls nothing on the tree may record **no** work there — not even their own. To
  work a node they don't control, they must first **pick it up** (§4.3) or be given a node they own.
- An **unassigned** (`NULL`) node is not directly workable by a plain Worker: `controls` is false for
  everyone, so recording is denied until someone claims it. Admin/JobManager may still record on it
  directly (their bypass does not depend on `controls`).

`worked_by_user_id` remains a free choice of the authorized recorder; the per-`(worked_by_user_id,
leaf_work_id)` overlap constraints (schema version 0007) are unchanged and orthogonal to this policy.

This capability was always the domain's own rule, but the staff UI did not expose a way to invoke it
until the browse-sessions plan added a "Start for…" worker picker (a batched, server-computed
`CanManageSessions` rendering hint gates whether the picker is shown at all — never the authority
itself, which the command re-evaluates from `canRecordWork` above at write time regardless of what
the page rendered). Being able to *see* a leaf's recorded sessions is unrelated and unconditionally
open to every employee role (ADR 0041) — a Worker who controls nothing on a leaf can still read its
whole history, just not add to or correct it.

A leaf being **closed** to new sessions — terminal achievement or archived (ADR 0044) — is an
orthogonal condition to authorization: it is checked independently of, and in addition to,
`canRecordWork` above. An owner who would otherwise be authorized to start or start-for a session is
still refused if the leaf itself is closed; reopening/restoring the leaf and holding `canRecordWork`
are both required, neither is a substitute for the other.

### 4.3 Pickup — claim an unassigned node

```
canPickUp(actor, node) =
       node.owner_user_id IS NULL
    AND (actor has Worker OR actor has JobManager OR actor has Administrator)
```

A successful pickup sets `owner_user_id = actor` and removes the node from the pool. Ancestors are
**irrelevant** to the pickup right — a `NULL` node is always claimable by anyone (§2.1). Pickup is an
explicit, audited action; it is the only way a plain Worker converts a pool node into one they may
work. Pickup does **not** require the node to be "ready" (readiness gates *starting a session*, not
claiming the node).

Pickup is allowed for branches as well as leaves. Claiming an unassigned branch makes the claimant a
controller for that branch's entire subtree, including descendants that are themselves unassigned or
directly owned by someone else. That follows the same ancestor rule as any other direct ownership.

Pickup is a conditional, concurrency-safe write: `SET owner = @actor WHERE id = @id AND owner IS
NULL`. If the row was claimed by someone else first, the update affects zero rows and the command
throws `already-claimed` rather than silently overwriting.

### 4.4 Reassignment and release to the pool

The controlling manager of a node (anyone for whom `canManageStructure` is true) may set its direct
owner to **any user** or to **`NULL`** (release back to the pool). This intentionally relaxes the old
spec rule (`:303`) that restricted reassignment to Admin/JobManager: a Worker who controls a subtree
may hand any node in it to another user, or park it as unassigned. Because ancestor ownership
cascades, an ancestor owner may reassign or release a descendant even when that descendant has a
different direct owner. The permanent root may never be released to `NULL`.

Reassignment/release is always explicit and audited. Audit events must record the acting user and the
before/after `owner_user_id`; work-session audit events must likewise distinguish the actor who
entered or corrected the session from the `worked_by_user_id`.

## 5. Role summary under this model

| Role | Structure | Work recording | Pickup | Reassign owner |
|---|---|---|---|---|
| Administrator | Any node | Any node, any worker | Any unassigned node | Any node → any user / `NULL` |
| JobManager | Any node | Any node, any worker | Any unassigned node | Any node → any user / `NULL` |
| Worker | Nodes they control | Nodes they control, any worker | Any unassigned node | Nodes they control → any user / `NULL` |
| Requester | — | — | — | — |
| RateManager / CostViewer / Auditor | — | — | — | — |

Administrator and JobManager retain hierarchy-wide authority (unchanged); ownership gates the Worker
role only. Requester is a proposed additional role for client intake (§9); it does not participate
in technical ownership or work authorization.

### 5.1 Reading is not owner-gated

The table above is entirely about **writing**. Ownership gates nothing on the read side: spec §7.3
gives every employee role, `Worker` included, an unqualified "view employees and job data" baseline,
and every restriction in that role's row concerns managing. Concretely:

| Read | Who | Gate |
|---|---|---|
| Job tree, node detail, achievement, prerequisites, readiness | Any employee | None (the web's broad `AnyEmployee` policy) |
| A leaf's work sessions — every worker's, not just your own | Any employee | `WorkSessionAccessPolicy.CanView` (ADR 0041) |
| Cost on a node | Administrator/CostViewer, **or** an owner of the node or an ancestor | `CostAccessPolicy.CanView` (ADR 0040) |
| One **individual leaf's** cost, inside a subtree already admitted above | As above, but another worker's leaf is redacted | `CostAccessPolicy.CanViewNodeCost` (ADR 0042) |

`Requester` is outside all of this — it sees only the read-only projection of its own requests (§9).

So the model is **"anyone may look, only controllers may change"**, with cost the one read that
carries its own gate, because an individual leaf's cost plus its (visible) session hours would
otherwise reveal that worker's hourly rate. A branch's roll-up stays visible to anyone admitted to
the subtree — it is an aggregate, so no individual's rate is recoverable from it.

Note this is a *derived* boundary, not a stored one: a branch has no stored status or cost. Both are
computed from its descendant leaves at read time (spec §5.2, §10; ADR 0035), which is why a roll-up
can be shown while the leaf-level figures behind it are withheld.

## 6. Worked examples

- **Jack and Peter on one job.** Jack owns leaf L. Both Jack and Peter physically work it. Jack (the
  owner) records sessions with `worked_by_user_id = Jack` and others with `worked_by_user_id = Peter`.
  Peter, if he owns nothing on this tree, cannot self-record on L. Overlap is still checked
  per-person, so Jack's and Peter's sessions on L may overlap in wall-clock time.
- **Letting Peter self-manage.** Jack owns branch B. He creates a child leaf under B and assigns it
  to Peter (§4.4). Peter now controls that leaf and records his own (and others') time on it, without
  touching the rest of B. Jack still controls that leaf through ancestor ownership.
- **Open pool.** A JobManager creates leaf U with `owner = NULL`. It appears in the pickup pool. Any
  worker may claim it; the first to do so becomes its owner and may then record work. A second
  claimant gets `already-claimed`.
- **Unassigned under an owned branch.** Alice owns branch B; a descendant leaf D has `owner = NULL`.
  Alice controls D (ancestor rule) and may record work on it directly. D is *also* in the public pool
  and Bob may claim it; if Bob does, he becomes D's owner while Alice still controls D via B.
- **Branch pickup.** Branch P is unassigned and has two descendant leaves, one unassigned and one
  owned by Carol. Bob picks up P. Bob now controls P and both descendants through ancestor ownership;
  Carol remains the direct owner, and therefore also a controller, of her own leaf.
- **Ancestor reassignment.** Alice owns branch B and Peter directly owns descendant leaf L. Alice may
  reassign L to Carol or release L to the pool because Alice controls L through B. Peter may reassign
  L only while he still controls L.

## 7. What changes from the current implementation

- `job_node.owner_user_id` becomes nullable (schema versions 0004, both providers); the permanent
  root gains a "root owner is non-null" guard.
- `WorkSessionAccessPolicy.CanManage(roles, isOwnSession)` becomes
  `CanManage(roles, actorControlsNode)` — a **breaking change** to a `JobTrack.Domain` public type,
  reviewed against the Framework Design Guidelines and closed by ADR 0032.
- The work-session command ports stop comparing `actorId == workedByUserId` and instead compute
  `controls(actor, leaf)` (reusing the ancestor-owner walk the job-node port already performs).
- Query/read models and public DTOs represent `owner_user_id = NULL` deliberately, and owner filters
  distinguish "no owner filter" from "only unassigned".
- New `PickUp` (claim) command on `IJobCommands`/`IJobTrackClient`; owner reassignment/release
  surfaced through the existing edit path with the relaxed authority of §4.4.
- Spec updates: `:85` (owner optional, `NULL` = unassigned), `:296` (Worker work-session wording),
  `:303` (relaxed reassignment + pool/pickup).

## 8. Assumptions confirmed by implementation

These were captured as assumptions while the plan was still proposed; all three shipped as stated,
closed by ADR 0031, and are no longer open:

1. **Pickup role scope.** Any holder of `Worker` (plus Admin/JobManager) may pick up. The pure
   read-only/cost roles (RateManager, CostViewer, Auditor) may not, since they cannot work
   (`JobPickupPolicy.CanPickUp`).
2. **Release-to-pool authority.** Identical to `canManageStructure` (§4.4) — the same
   `EditJobNodeRequest` path both reassigns and releases, gated the same way.
3. **Pickup granularity.** Any unassigned node (branch or leaf) is claimable, not leaves only,
   verified by the branch-pickup contract test asserting control cascades to descendants.

## 9. Requester/client intake extension

Status: **proposed**, tracked by
[`plans/2026-07-11-client-requester-intake-plan.md`](plans/2026-07-11-client-requester-intake-plan.md).

The system needs a distinct low-permission interface for people who post IT problems but are not
technical staff. These users should be able to create a request, monitor progress, and supply
clarifying information without being able to pick up work, record sessions, browse unrelated
operational work, or see rates/costs/audit detail.

Introduce a seventh role:

```text
Requester
```

Use `Requester` in code and documentation rather than `Client`: the permission model is "can submit
and monitor requests", not "belongs to an external customer tenant." The initial product remains a
single-organisation employee-account system unless a separate multi-tenancy decision changes that.

Requester is deliberately excluded from all technical control predicates:

```text
canManageStructure(Requester, node) = false
canRecordWork(Requester, node) = false
canPickUp(Requester, node) = false
```

Adding Requester therefore must not widen any existing Worker/JobManager/Admin authority. It adds a
separate intake surface and a separate progress projection.

## 10. Holding areas

A **holding area** is a configured `JobNode` parent that accepts requester-created children. It is
the route from a department or requester population into the operational hierarchy.

Holding areas are configuration, not technical ownership. A holding area should carry:

| Field | Meaning |
|---|---|
| `Id` | Stable holding-area identifier. |
| `JobNodeId` | Parent node under which requester-created jobs are inserted. |
| `Name` | Display label for requester/staff selection. |
| `DepartmentId` | Optional department route; `NULL` means globally available if policy allows it. |
| `DefaultPriorityId` | Priority applied to submitted requests unless requester priority hints are enabled. |
| `DefaultOwnerUserId` | Optional technical owner; `NULL` means submitted jobs enter the unassigned pool. |
| `IsActive` | Disabled holding areas reject new submissions but preserve historical requests. |
| `Version` | Optimistic concurrency token for configuration edits. |

Creation policy:

- requester-created jobs are direct children of the selected holding-area node;
- `PostedByUserId` is the requester;
- `OwnerUserId` is supplied from `DefaultOwnerUserId`, usually `NULL` for unassigned triage;
- server-side configuration supplies parent, priority, timestamps, and default owner;
- requester input is allow-listed and cannot mass-assign parent, owner, posted-by, priority,
  schedule, rate, cost, achievement, prerequisite, leaf-work, or work-session state; and
- no `LeafWork` is created by default unless a later product decision says every request is directly
  actionable leaf work.

The configured holding-area `JobNodeId` must be a real node in the operational hierarchy. It should
normally be a branch controlled by JobManager/Admin or by an intake team owner. It cannot be the
permanent root unless an explicit product decision accepts root-level requester children.

## 11. Request anchors

Use a separate **request anchor** to preserve requester ownership/monitoring independently of
technical assignment.

Proposed shape:

```text
job_request(
    job_node_id primary key references job_node(id),
    requester_user_id references app_user(id),
    holding_area_id references request_holding_area(id),
    requester_reference nullable,
    submitted_at,
    closed_to_requester_at nullable,
    version
)
```

Rationale:

- `PostedByUserId` remains useful, but not every posted job is a client request.
- `job_node.owner_user_id` remains technical control, not requester ownership.
- Reassignment, pickup, move, and decomposition can happen without losing the requester's monitoring
  link.
- Staff can split the technical work into a subtree while the original request remains the stable
  thing the requester follows.

Moving a requester job changes the original `job_node.parent_id`; it does not leave a duplicate or
placeholder job under the holding area. The separate `job_request` row remains attached to the same
`job_node_id`, and its `holding_area_id` is submission metadata: where the request entered the
system, not the node's required current parent.

If the request node is decomposed, the `job_request` row stays anchored to the original request node
and requester progress is derived from that node's subtree. Do not migrate the request anchor to a
child merely because the technical implementation changed.

## 12. Department routing

Department routing is authorization data. Do not use free-text department names for access control.

Proposed minimal shape:

```text
department(id, name, is_active, version)
app_user_department(app_user_id, department_id, is_primary)
request_holding_area(..., department_id nullable, ...)
```

Routing rules:

- a Requester with exactly one eligible holding area may be routed there by default;
- a Requester with multiple eligible holding areas chooses from an allow-listed set;
- a Requester with no eligible holding area cannot submit and receives a clear operational error;
- inactive departments or holding areas reject new submissions but retain existing visibility; and
- changes to department membership and holding-area configuration are audited.

Open decision: whether department-scoped request visibility is enabled in the first release. If it
is not enabled, Requesters see only requests where they are `requester_user_id`.

## 13. Requester authorization predicates

Requester predicates are evaluated from authoritative stored state: current roles, enabled account
state, department membership, holding-area configuration, and request anchor. Client-supplied claims
or form fields do not participate except as identifiers to look up.

```text
canSubmitRequest(actor, holdingArea) =
       actor has Requester
    AND holdingArea is active
    AND actor is eligible for holdingArea

canViewRequest(actor, request) =
       actor is request.requester_user_id
    OR (department request visibility enabled
        AND actor has Requester
        AND actor belongs to request department)
    OR actor has Administrator
    OR actor has JobManager
    OR (actor has Worker AND controls(actor, request.job_node_id))

canCommentAsRequester(actor, request) =
       actor has Requester
    AND canViewRequest(actor, request)
    AND request.closed_to_requester_at IS NULL
```

Requester visibility is a limited projection, not full job browse. It may include:

- request id / job node id;
- title and submitted detail;
- submitted timestamp;
- holding-area name;
- requester-facing status;
- last requester-visible update timestamp;
- a read-only tree projection rooted at the request node, including descendants created when staff
  split the request into technical sub-jobs;
- optional team/queue label;
- optional completion outcome; and
- requester-visible notes posted by staff or by the requester.

It must not include:

- rates or costs;
- work-session detail;
- employee schedules;
- audit internals;
- unrelated jobs, sibling nodes, ancestor nodes, or any node outside the request subtree;
- full employee directories; or
- staff-only comments, private triage notes, or internal-only operational fields.

Requester subtree visibility is **read-only**. It does not grant `canManageStructure`,
`canRecordWork`, `canPickUp`, reassignment, achievement transition, prerequisite edit, move,
decomposition, archive, or work-session permissions. It only exposes the request's own node and
descendants through a requester-safe shape. Requesters may see that a request has been split into
children and may see each child node's requester-facing title/status, but not staff-only fields.

Suggested requester-facing statuses:

```text
Submitted
Accepted
InProgress
Waiting
Completed
Cancelled
```

Open decision: whether `Accepted` is persisted as an explicit staff action or inferred from
assignment/movement out of the holding area. Prefer explicit acceptance if requesters need to know
that IT has seen the request.

## 14. Requester worked examples

- **Single department intake.** Emma has Requester and belongs to Finance. Finance has one active
  holding area, "Finance IT Requests", whose default owner is `NULL`. Emma submits "Laptop VPN
  failure." The system creates a child under that holding node with `PostedByUserId = Emma` and
  `OwnerUserId = NULL`, plus a `job_request` anchor. Emma can see the request progress page but
  cannot pick up or edit the job.
- **Triage assignment.** A JobManager views the Finance holding queue and assigns Emma's request to
  Ravi. Ravi now controls the technical job as a Worker. Emma remains the requester because the
  `job_request` anchor is unchanged.
- **Technical decomposition.** Ravi splits the request into "Collect logs" and "Reconfigure VPN
  profile." The original node becomes the request branch; the `job_request` remains attached to it.
  Emma sees a read-only requester-safe tree rooted at the original request, including both child
  jobs and their requester-facing statuses. She cannot edit, pick up, assign, record work, or see
  rates, sessions, audit internals, staff-only notes, or unrelated siblings.
- **Completion notes.** Ravi completes "Reconfigure VPN profile" and publishes a requester-visible
  note: "VPN profile rebuilt; restart required." Emma can read that note from her request detail
  page. A separate private triage note remains invisible to her.
- **Wrong department.** Noah belongs to HR and attempts to submit into Finance's holding area by
  posting a crafted `holdingAreaId`. The command reloads membership and holding-area eligibility and
  rejects the submission.
- **Requester is not Worker.** Emma attempts to call pickup or start-session endpoints against her
  own request. Both are denied because Requester does not participate in technical control.

## 15. Consequences for the current model

Adding Requester requires revisiting the existing broad read rule in `jobtrack_spec_codex.md` §7.3:
"All authenticated employees may view ... job ... information" is no longer correct for Requester
accounts. Keep that broad operational visibility for Worker/JobManager/Admin/Auditor as decided, but
add a narrower requester projection for Requester.

Implementation must update:

- role reference data and `EmployeeRole`;
- account provisioning to permit Requester as an initial role;
- holding-area and department configuration commands;
- request submission commands;
- request progress queries;
- requester-safe read-only subtree and notes queries;
- direct HTTP/API endpoint policies;
- browser navigation so Requester accounts land on request pages, not operational job browse;
- tests proving Requester cannot reach operational job/work/rate/cost/audit surfaces; and
- traceability and threat-model entries for requester data isolation and request-text handling.
