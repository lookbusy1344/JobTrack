# Client requester intake, holding areas, and limited progress tracking

**Date:** 2026-07-11  
**Status:** Core submission/triage/move/UI/API slice complete (Stages 1-8): ADR 0033/spec amendments,
database contracts, domain/application policy, `SubmitAsync`/`MoveAsync` persistence ports, the
requester web UI, the external HTTP API (`GET /api/request-holding-areas`, `POST /api/requests`,
`GET /api/requests`, gated by `RequesterAccess`), the extended `JobTrack.ExternalApiClient`
first-party proof, and the requester-intake threat-model review
(`docs/threat-model/web-authentication-threat-model.md` §7/§8, rows 20-25). The full solution test
suite passed with no failures as the Stage 8 gate. `/Requests/{id}` and its API equivalent are closed
by Stage 9 below (ADR 0034: explicit `acknowledged_at` acceptance, the `job_request_note` table, and
the requester-safe subtree query). Still deliberately deferred, tracked as follow-up work rather than
left silently incomplete: the staff holding-area queue UI (backed by the existing `/Jobs/Browse`-style
children query — no new backend needed), decomposition-preserves-anchor UI evidence, and an explicit
assignment test (existing `EditAsync` already covers setting `OwnerUserId`).  
**Depends on:** accepted ownership model (`docs/ownership-model.md`), ADR 0031/0032, and the web/API
authorization boundary in `docs/jobtrack_spec_codex.md` §7.3 and §13.6.

## 1. Current answer

The application does **not** currently have a distinct end-user/client interface for someone whose
only job is to post an IT problem and monitor its progress.

The current model has:

- employee-only accounts provisioned by an administrator;
- six baseline roles: Administrator, JobManager, Worker, RateManager, CostViewer, Auditor;
- `PostedByUserId` on `JobNode`;
- nullable `OwnerUserId`, where `NULL` means an unassigned pickup-pool job; and
- Worker/JobManager/Admin assignment, pickup, reassignment, and owner-gated work recording.

That supports technical staff intake and assignment, but it is too permissive and too operational
for ordinary requesters. In particular, `jobtrack_spec_codex.md` §7.3 currently says all
authenticated employees may view job data. A low-permission requester needs a narrower surface:
create a request into a configured holding area, see only their own requests (and possibly requests
submitted by their department), add limited requester comments or cancellation requests, and never
gain Worker powers by accident.

## 2. Target model

Add a **Requester** role for client/end-user accounts.

A Requester can:

- create a new unassigned job under an allowed holding-area parent;
- view a flat list of requests they posted, and optionally department requests if explicitly
  configured;
- open a request detail page containing a requester-safe read-only tree rooted at the request node,
  including descendants created if staff split the job into sub-jobs;
- read requester-visible notes attached by completing or triaging staff;
- add requester-visible notes or clarifications;
- request cancellation or closure, subject to staff confirmation; and
- update their own contact/profile fields if the account model permits it.

A Requester cannot:

- pick up a job;
- own a job as a technical controller;
- record work sessions;
- view rates, costs, schedules, employee lists, audit detail, or unrelated jobs;
- reassign jobs;
- move/decompose/archive jobs;
- edit prerequisites, leaf work, achievements, or work sessions; or
- browse the operational tree outside their permitted request subtrees.

Technical staff then triage the request from the holding area:

- JobManager/Admin assigns the node to a Worker, leaves it unassigned for pickup, or moves it into
  an operational branch.
- The assigned node owner, an owner of any ancestor of the node, JobManager, or Administrator can
  move the requester job from the holding area to any valid parent without also owning the
  destination parent.
- JobManager/Admin or a controlling Worker can decompose it into child nodes/subtrees using the
  existing explicit decomposition/child-creation model. This plan uses **JobTrack job subtrees**,
  not git worktrees; this repository's operating guidance explicitly says we do not use git
  worktrees.
- The original request ownership/visibility is preserved through `PostedByUserId` and/or an
  explicit requester link so the requester can continue monitoring progress after staff move or
  split the technical work. If the request is split into a subtree, the requester can view that
  subtree through a read-only requester-safe projection.

## 3. Holding areas

A **holding area** is a configured `JobNode` parent that accepts requester-created children. It is
not a new hierarchy type unless implementation evidence shows that `JobNode` metadata is
insufficient.

Each holding area has:

- a stable identifier;
- a `JobNodeId` parent under which requester requests are created;
- a display name;
- enabled/disabled state;
- department or requester eligibility rules;
- default node kind and priority;
- optional default needed-start/needed-finish policy;
- optional staff owner or `NULL` owner policy;
- optional notification/triage metadata; and
- optimistic concurrency and audit history.

Default creation policy:

- requester-created jobs are direct children of the configured holding-area node;
- `PostedByUserId` is the requester;
- `OwnerUserId` is `NULL`, placing the request in the unassigned pool unless the holding-area
  configuration supplies a staff owner;
- no `LeafWork` is created by default unless the product decision is that every request is directly
  actionable work;
- requester-supplied fields are allow-listed: title/description, optional detail, contact/context,
  priority hint if enabled, and attachments only if a separate attachment threat model is accepted;
- server-side defaults supply `Kind`, `Priority`, timestamps, and parent.

Department routing:

- each requester account may belong to zero or more departments;
- each department may have one default holding area;
- a requester with one eligible holding area is routed there by default;
- a requester with multiple eligible holding areas must choose from an allow-listed set;
- a requester with no eligible holding area cannot create a request and receives an operationally
  actionable error, not a fallback to root.

Open product decision: whether departments are first-class persisted entities or a simpler account
attribute/reference table for the initial release. Prefer a small `department` reference table if
department-scoped visibility is required; avoid free-text department names for authorization.

## 4. Data model changes

Add the minimum schema needed to express requester routing and visibility without overloading
technical ownership.

Proposed tables/fields:

- `department`
  - `id`
  - `name`
  - `is_active`
  - `version`
- `app_user_department`
  - `app_user_id`
  - `department_id`
  - optional `is_primary`
- `request_holding_area`
  - `id`
  - `job_node_id`
  - `department_id` nullable if globally available
  - `name`
  - `default_priority_id`
  - `default_owner_user_id` nullable
  - `is_active`
  - `version`
- `job_request`
  - `job_node_id` primary key and foreign key to `job_node`
  - `requester_user_id`
  - `holding_area_id`
  - optional `requester_reference`
  - `submitted_at`
  - optional `closed_to_requester_at`
  - `version`

Rationale:

- `PostedByUserId` already exists, but an explicit `job_request` row prevents every posted job from
  becoming a requester-ticket candidate forever.
- Technical ownership remains `job_node.owner_user_id`; requester ownership/monitoring is separate.
- Moving or decomposing the technical job does not break requester visibility because the request
  anchor remains stable.

If staff decompose the original request node into technical children, the `job_request` remains on
the original request node. Requester progress and tree visibility are computed from that node's
subtree, not from only the current direct leaf.

## 5. Moving requester jobs

Moving requester jobs is an in-scope staff triage operation, not a later enhancement.

Permission rule:

```text
canMoveRequesterJob(actor, node, newParent) =
       actor has Administrator
    OR actor has JobManager
    OR (actor has Worker AND controls(actor, node))
```

`newParent` is intentionally **not** part of the authorization predicate. The actor needs control of
the node being moved, not control of the destination parent. This keeps the model usable for the
main intake workflow: a worker assigned a holding-area request can re-home it under the branch where
the work logically belongs as the problem becomes clearer.

The destination is still validated by existing hierarchy/workflow invariants:

- the permanent root cannot be moved;
- the new parent must exist;
- the new parent cannot be the moved node or one of its descendants;
- the new parent cannot already have `LeafWork`;
- the move must not create a prerequisite ancestor/descendant violation;
- the move uses optimistic concurrency on the moved node; and
- the move is audited with before/after parent ids and the acting user.

Moving a requester job updates the original `job_node.parent_id`; there is no duplicate job left in
the holding area. The separate `job_request` metadata row remains attached to the same
`job_node_id`, and `job_request.requester_user_id` plus `job_request.holding_area_id` do not change.
`holding_area_id` records where the request entered the system, not where the job must remain in the
hierarchy.

## 6. Authorization rules

Introduce `EmployeeRole.Requester` unless product language strongly prefers `Client`. Use
`Requester` in code because it describes the actor's permission model without implying an external
customer identity boundary.

Requester predicates:

```text
canSubmitRequest(actor, holdingArea) =
       actor has Requester
    AND holdingArea is active
    AND actor is eligible for holdingArea

canViewRequest(actor, request) =
       actor is request.requester_user_id
    OR (department visibility enabled AND actor has Requester AND actor is in request department)
    OR actor has Administrator
    OR actor has JobManager
    OR actor has Worker and controls(request.job_node_id)

canCommentAsRequester(actor, request) =
       canViewRequest(actor, request)
    AND actor has Requester
    AND request is not requester-closed
```

Requester is deliberately excluded from:

- `JobPickupPolicy.CanPickUp`;
- `JobNodeAccessPolicy.CanManage`;
- `WorkSessionAccessPolicy.CanManage`;
- rate/cost/audit administration policies;
- schedule-management policies except their own profile/schedule if a later product decision wants
  requesters to also be employees with schedules.

Existing read queries must no longer treat "any authenticated employee" as sufficient for full job
browse once Requester exists. Add a limited requester projection instead of relaxing the operational
queries.

## 7. Requester progress projection

Do not expose the full operational node, employee, cost, rate, audit, schedule, or session model to
requesters. Add a specific query result, for example `RequesterJobProgressResult`, containing only:

- request id / job node id;
- title/description;
- submitted timestamp;
- holding area display name;
- current public status;
- last public update timestamp;
- a requester-safe read-only tree rooted at the request node, including descendant sub-jobs created
  during decomposition;
- optional assigned team/queue label, not individual staff details unless approved;
- optional completion outcome; and
- requester-visible notes posted by staff or by the requester.

The requester-safe tree projection must not expose the full operational job model. Each visible node
should carry only a small public shape:

- job node id;
- title/description or requester-facing summary;
- requester-facing status;
- parent/child relationship inside the request subtree;
- last requester-visible update timestamp; and
- requester-visible notes for that node.

It must not include rates, costs, work sessions, schedules, audit events, staff-only notes, unrelated
siblings, ancestors outside the request subtree, or edit controls. The projection is read-only and
must not be served by relaxing `/Jobs/Browse`.

Public status should be derived from existing job state but mapped to a small requester vocabulary:

- `Submitted`
- `Accepted`
- `InProgress`
- `Waiting`
- `Completed`
- `Cancelled`

Resolved at Stage 9 (ADR 0034): `Accepted` is a separate persisted staff action
(`job_request.acknowledged_at`/`acknowledged_by_user_id`, set by `AcknowledgeAsync`), not inferred
from assignment/move out of holding, so requesters can distinguish "seen by IT" from "still sitting
in intake".

## 8. API and web surface

Browser UI:

- `/Requests/New`: simple form for a requester to post an IT problem.
- `/Requests`: flat list of the requester's own posted jobs, with department filter only when
  authorized.
- `/Requests/{id}`: detail view with requester-safe progress, requester-visible staff notes, and
  the read-only request subtree when the job has been split.
- Staff browse/triage page: holding-area filter showing requester-created unassigned jobs.
- Admin configuration page: manage departments and holding areas.

HTTP API:

- `GET /api/request-holding-areas` returns only holding areas the actor may submit to.
- `POST /api/requests` creates a requester job under a permitted holding area.
- `GET /api/requests` lists the actor's permitted request projections.
- `GET /api/requests/{jobNodeId}` returns one permitted request projection, including the
  requester-safe read-only subtree.
- `POST /api/requests/{jobNodeId}/comments` adds a requester-visible note or clarification.

All endpoints call `IJobTrackClient`; none reach persistence directly. Endpoint policies provide
coarse admission, and the library reloads roles, account state, department membership, holding-area
configuration, and job/request state authoritatively inside the operation.

## 9. TDD implementation plan

### Stage 1 - ADR and spec amendment

- Add an ADR for requester intake, holding areas, and limited progress visibility.
- Amend `jobtrack_spec_codex.md` §7.1/§7.3/§13.6 to add Requester and to narrow full job browsing
  away from requester accounts.
- Update `docs/database-entities.md` with the new department/holding/request entities.
- Update `docs/traceability/test-catalogue.md` with stable test IDs before implementation.

### Stage 2 - Database contracts

Tests first, shared contract suite:

- roles reference data includes Requester;
- departments enforce unique active names if required;
- holding areas reference existing job nodes and active default kind/priority;
- requester submissions require an existing requester and holding area;
- a request anchor cannot point at a root node;
- deleting request anchors is restricted consistently with job retention;
- PostgreSQL and SQLite enforce the same observable constraints;
- concurrent request creation into the same holding area produces distinct job nodes and request rows.

Implementation:

- edit existing pre-release schema-version scripts in place if this lands before first release;
- add EF entities/configuration in the shared persistence model;
- add provider-specific error translation for named constraints/triggers.

### Stage 3 - Domain/application policy

Tests first:

- Requester can submit only to eligible active holding areas;
- Requester cannot submit to inactive or unauthorized holding areas;
- Requester cannot pick up, assign, record work, view costs, or browse unrelated operational nodes;
- Admin/JobManager can configure holding areas;
- staff who can see/control the technical node can see the requester context needed for triage.

Implementation:

- add `EmployeeRole.Requester`;
- add pure policy types for requester submit/view/comment/configure;
- add request/holding-area command and query DTOs with XML docs and documented exceptions;
- add methods under `IJobTrackClient.Requests` or a cohesive equivalent facade section.

### Stage 4 - Persistence command/query ports

Tests first on both providers:

- `SubmitRequestAsync` creates `job_node` plus `job_request` atomically;
- actor and department eligibility are reloaded in the transaction;
- caller-supplied parent, owner, kind, priority, posted-by, and timestamps cannot be mass-assigned;
- default owner `NULL` creates an unassigned request visible in staff intake/pickup views;
- default owner non-null assigns directly to configured staff/team owner;
- requester progress still resolves after the request node is moved or decomposed;
- requester-safe subtree queries include descendants after decomposition but exclude unrelated
  siblings and ancestors;
- requester-visible staff notes are returned, while private triage notes are not.

Implementation:

- create the job via the existing job-node write machinery where possible;
- add a request-specific transaction method only where existing command shape is too privileged;
- audit request submission and holding-area configuration changes.

### Stage 5 - Staff triage and node movement workflow

Tests first:

- JobManager sees holding-area queues;
- JobManager assigns a requester job to a Worker;
- the assigned node owner can move the requester job from its holding area to any valid parent
  without owning the destination parent;
- an owner of an ancestor of the requester job can move it to any valid parent without owning the
  destination parent;
- JobManager moves/decomposes a requester job while the `job_request` anchor remains intact;
- moving still rejects invalid destinations: root move, cycle, nonexistent parent, parent with
  `LeafWork`, or prerequisite ancestor/descendant violation;
- Worker with control can update technical progress but cannot alter requester identity/routing;
- completing staff can add requester-visible notes;
- Requester sees progress changes, the read-only request subtree, and requester-visible notes, but
  not internal work-session/rate/audit details or private triage notes.

Implementation:

- add staff filters for holding areas and requester-originated jobs;
- surface requester context on staff detail pages without exposing unrelated requester PII;
- update `MoveAsync` implementation/tests if it still requires destination-parent control;
- expose move controls in staff triage/detail pages for users who control the moved node;
- reuse existing assignment, pickup, edit, move, and decomposition commands.

### Stage 6 - Requester web UI

Tests first:

- Requester can open the new-request page and submit a valid request;
- validation rejects blank description and unauthorized holding area;
- Requester sees a flat list of only their own permitted requests;
- Requester can open a request and view its read-only subtree after staff decomposition;
- Requester can read staff-published requester-visible notes;
- Requester cannot reach `/Jobs/Browse`, work-session, rate, cost, audit, account-admin, or
  holding-area-admin surfaces;
- accessibility/axe checks pass for the new pages.

Implementation:

- compose existing Console design-language primitives;
- keep the first screen task-focused: list current requests and a compact new-request action;
- avoid exposing operational tree edit controls in requester pages.

### Stage 7 - External HTTP API and client proof

Tests first:

- bearer-token Requester can submit and list own requests;
- bearer-token Requester can read the requester-safe tree and requester-visible notes for their own
  request;
- bearer-token Requester cannot call operational job/work/rate/cost endpoints;
- wrong-department and wrong-request access returns the documented problem response;
- OpenAPI route set includes the requester endpoints and excludes privileged fields from requester
  schemas;
- first-party external client proof exercises the request submission/read flow without referencing
  `JobTrack.*` assemblies.

Implementation:

- add request endpoints and models;
- update OpenAPI docs/reference;
- extend the sample external client with a requester scenario.

### Stage 8 - Gate evidence

Run the gates in architectural order:

- database contract/provider tests for new schema and constraints;
- library unit and provider-conformance tests for policy and command/query behavior;
- web integration and end-to-end tests for direct-request authorization and UI;
- external API client proof;
- security/threat-model update covering requester data isolation, mass assignment, CSRF, rate
  limiting, XSS in request text, and potential attachment handling.

### Stage 9 - Request detail page: acceptance, notes, subtree (ADR 0034)

Tests first, database contracts:

- `job_request.acknowledged_at`/`acknowledged_by_user_id` accept a null default and a single set;
- `job_request_note` rows require an existing `job_node`/author, reject blank content, and reject
  update/delete (append-only, like `audit_event`);
- both providers enforce the same constraints.

Tests first, domain:

- `RequesterStatusCalculator` covers every precedence branch (Completed/Cancelled/InProgress/
  Waiting/Accepted/Submitted) including mixed-subtree cases (some leaves succeeded, one still
  waiting; all leaves terminal-negative; no `LeafWork` anywhere yet).

Tests first, persistence (both providers):

- `AcknowledgeAsync` sets the timestamp/actor exactly once, is idempotent-safe under optimistic
  concurrency, and is denied to a non-controlling actor;
- `AddNoteAsync` as staff can write a private or requester-visible note; as the requester always
  writes a requester-visible note; is denied once the request is closed to the requester or the actor
  has no view access;
- `GetDetailAsync` returns the requester-safe subtree (post-decomposition) and only requester-visible
  notes for a requester caller, and every note for a staff/admin caller.

Tests first, external API and web UI:

- `GET /api/requests/{jobNodeId}`, `POST /api/requests/{jobNodeId}/comments`, and
  `POST /api/requests/{jobNodeId}/acknowledge` cover requester/staff success paths, cross-request
  denial, and the non-`RequestDetailAccess` role rejection;
- `/Requests/{id}` renders status, subtree, and notes with a reply box, passes the axe scan, and is
  linked from `/Requests`.

Implementation: schema edits in place (0020/0014), `RequesterStatusCalculator`, the four new
`IJobRequestCommandPort` methods on both providers following `MoveAsync`'s transaction/audit
skeleton, `JobNodeHierarchyQueries.GetRequesterSubtreeAsync`, the three new API endpoints plus
`JobTrackPolicyNames.RequestDetailAccess`, the `Details.cshtml` page, and the external client proof
extension.

## 10. Security and privacy notes

- Request text is untrusted user content. Render encoded text only; no HTML by default.
- If attachments are introduced, they require a separate plan for storage, scanning, content-type
  handling, size limits, retention, and download authorization.
- Department membership is authorization data and must be audited when changed.
- Requester views must avoid leaking staff schedules, rates, costs, audit internals, unrelated
  employee lists, or sibling jobs in the operational tree.
- Request submission should have rate limiting distinct from login throttling and external API
  throttling.
- Logs must contain request ids and actor ids, not unrestricted request descriptions.

## 11. Out of scope

- Public self-registration.
- Anonymous request submission.
- External customer multi-tenancy.
- Email ingestion or mailbox polling.
- SLA/escalation timers.
- Attachments.
- Chat-style two-way messaging beyond requester-visible staff notes and requester clarifications.
- Git worktrees or source-control workflow changes.

Those may be added later, but they should not be smuggled into the first requester intake slice.
