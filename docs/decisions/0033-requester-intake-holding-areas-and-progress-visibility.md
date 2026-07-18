# ADR 0033: Requester intake, holding areas, and limited progress visibility

**Status:** Accepted
**Closes:** `docs/plans/2026-07-11-client-requester-intake-plan.md` (all sections); amends
`jobtrack_spec_codex.md` §7.1, §7.3, §13.6.

## Context

JobTrack's authorization model assumed every account belongs to technical staff: `jobtrack_spec_codex.md`
§7.3 grants "all authenticated employees may view other employees' non-secret profile, schedule, job,
and work information", and the six baseline roles (Administrator, JobManager, Worker, RateManager,
CostViewer, Auditor) are all operational roles. There is no account type for someone whose only need
is to post an IT problem and watch its status — using an existing role would either grant excess
operational visibility (any current role sees the full job tree) or require inventing ad hoc
per-endpoint carve-outs with no policy home.

There is also no configured entry point for requester-submitted work. `PostedByUserId` exists on
`JobNode`, but nothing currently distinguishes "a request a client is tracking" from "any job posted
by any employee", and nothing constrains where a low-permission account may create a node.

## Decision

Add `EmployeeRole.Requester` as a seventh baseline role (`= 7`, additive to the existing enum;
`docs/plans/2026-07-11-client-requester-intake-plan.md` §6 rationale for the name — `Requester`
describes the permission model, not an external customer identity boundary, so no separate account
type or authentication scheme is introduced). A Requester is still a locally managed ASP.NET Core
Identity account created by an administrator (§7.1 unaffected); it is a role grant, not a new
provisioning path.

Requesters submit into **holding areas** — configured `JobNode` parents (`request_holding_area`) that
accept requester-created children, scoped optionally by `department`. A new `job_request` row anchors
requester ownership/visibility to the created `job_node.id` independently of `job_node.owner_user_id`
(technical ownership) and independently of subsequent moves or decomposition, per the plan's §4–§5.

Full job browsing is no longer implied by "any authenticated employee": §7.3's blanket visibility
sentence is narrowed to exclude `Requester`. A Requester's visibility is instead a dedicated
requester-safe projection (`RequesterJobProgressResult`, plan §7) rooted at their own `job_request`
node(s), never a relaxation of `/Jobs/Browse` or the general job/work query surface.

Requester is excluded by construction from `JobPickupPolicy.CanPickUp`, `JobNodeAccessPolicy.CanManage`,
`WorkSessionAccessPolicy.CanManage`, and all rate/cost/audit/schedule-management policies other than
a Requester's own profile fields. Moving a requester job out of its holding area is authorized by
`canMoveRequesterJob` (plan §5) — Administrator, JobManager, or a Worker who controls the node being
moved — deliberately not gated on control of the destination parent, so the routine intake workflow
(claim from holding, re-home once the problem is understood) does not require pre-existing authority
over wherever the work turns out to belong. All of `MoveAsync`'s existing hierarchy/workflow
invariants (permanent root, no cycles, no `LeafWork` parent, prerequisite ancestor/descendant check,
optimistic concurrency, audit) still apply unchanged to a requester-originated move.

## Rationale

- A new role rather than a new authentication mechanism keeps requesters inside the existing
  Identity/session/audit/authorization machinery (§7.1's cookie, lockout, and audit rules apply to
  Requester accounts exactly as to any other role) instead of standing up a parallel client identity
  system for what is, mechanically, just another low-privilege role.
- Anchoring requester visibility to an explicit `job_request` row rather than reusing
  `PostedByUserId` as the requester-ticket marker keeps "who filed this" (a durable fact usable
  indefinitely) separate from "is this currently a requester-tracked ticket" (plan §4) — every
  `job_node` already carries `posted_by_user_id`, but not every posted node should forever behave as
  an open requester ticket.
- Narrowing §7.3's blanket employee visibility, rather than special-casing `Requester` at each query
  call site, follows the same default-deny posture §7.3 already states ("Authorization decisions
  shall default to deny") — the correct fix is to stop treating "authenticated employee" as
  sufficient, not to add a `Requester` exception on top of a rule that was already too broad for the
  new role.
- Making `canMoveRequesterJob` independent of destination-parent control (plan §5) mirrors the
  ownership model's existing precedent (ADR 0031): a controlling actor's structural authority over
  a node they control was already sufficient to archive or reassign it without separately controlling
  where it goes; move is not a bigger act of authority than those, and requiring pre-existing control
  of an as-yet-unknown destination would make the primary triage workflow (re-home once triage
  clarifies where work belongs) unusable in the common case.
- Holding areas as configured `JobNode` parents, not a new hierarchy type, avoid a second tree
  concept — the plan's §3 explicitly defers a new hierarchy type unless `JobNode` metadata proves
  insufficient, consistent with the project's preference for minimal structural additions.

## Consequences

- `jobtrack_spec_codex.md` §7.1 gains a note that a Requester is a role grant on the same
  employee-account model, not a separate identity boundary; §7.3's role table gains a `Requester` row
  and its blanket "all authenticated employees may view..." sentence is qualified to exclude
  `Requester`; §13.6 gains a note that the requester-safe projection is a distinct query surface, not
  a relaxation of the general job/work query surface.
- `database-entities.md` gains `department`, `app_user_department`, `request_holding_area`, and
  `job_request` table documentation (plan §4).
- `docs/traceability/test-catalogue.md` gains a `REQ` area tag and pending `TC-*` rows for the plan's
  §9 stages, filled in with real test IDs as each stage lands.
- `JobPickupPolicy`, `JobNodeAccessPolicy`, `WorkSessionAccessPolicy`, and rate/cost/audit policies
  require no code change to exclude `Requester` — they already deny anyone not holding fixed sets of
  the pre-existing roles the plan does not ask to grant to it — but the new role's absence from every
  such allow-list is explicitly asserted by the plan's §9 Stage 3 policy tests, not left implicit.
- Resolved at Stage 5: `IJobCommands.MoveAsync`/`IJobNodeCommandPort.MoveAsync` are unchanged — the
  ordinary structural move still requires control of both the moved node and the destination parent.
  `canMoveRequesterJob`'s destination-independence is **not** already implied by that existing check
  (a controlling Worker who does not also control the destination parent is denied by
  `IJobNodeCommandPort.MoveAsync` today), so a new, additive
  `IJobRequestCommandPort.MoveAsync`/`IRequestCommands.MoveAsync` was added instead of relaxing the
  general move. It first verifies the node has an associated `job_request` row (otherwise
  `InvariantViolationException("requester-job-required", ...)`), then authorizes with
  `JobNodeAccessPolicy.CanManage(actorRoles, controlsNode)` — the identical formula as ordinary
  structural moves, just evaluated only against the moved node, never the destination parent — before
  calling the same `move_job_node` stored function (PostgreSQL) / immediate `UPDATE`
  (SQLite) as the ordinary move, so every other invariant (cycle, no `LeafWork` parent, prerequisite
  ancestor/descendant check, optimistic concurrency, audit) is unchanged. This keeps the relaxation
  scoped to requester-originated nodes without touching the general move's already-tested behaviour.
- Stage 6 adds `JobTrackPolicyNames.RequesterAccess` (`RequireRole(EmployeeRoleNames.Requester)`) as
  a new named authorization policy alongside the existing six, and `EmployeeRoleNames.Requester =
  "Requester"` matching the schema version 0002 seed row. `JobTrackPolicyNames.AnyEmployee`'s role
  list is intentionally left unchanged (still the original six roles) rather than adding `Requester`
  to it — this is the concrete enforcement of §7.3's "all authenticated employees holding any role
  other than Requester" narrowing. `IRequestCommands` gained `GetMyRequestsAsync` (the requester's
  own submitted requests, most recent first) and `GetEligibleHoldingAreasAsync` (active holding areas
  the actor may submit into) to back the single combined `/Requests` Razor Page — list plus a compact
  new-request action, the same shape as the existing self-service `/Account/PersonalAccessTokens`
  page, not a separate `/Requests/New`. The requester-safe subtree/notes detail page (`/Requests/{id}`)
  remains deferred with the rest of the notes/comments schema work.
- Stage 7 exposes `GET /api/request-holding-areas`, `POST /api/requests`, and `GET /api/requests`
  under the external HTTP API, gated by `RequesterAccess`, reusing the same `ExecuteAsync` problem-
  translation/tracing helper every other endpoint already uses. `GET /api/antiforgery-token` moves
  from `AnyEmployee` to a new `AnyAuthenticatedUser` policy (`RequireAuthenticatedUser()`, no role
  restriction): the CSRF-token endpoint grants no operational capability by itself, and `AnyEmployee`'s
  role list deliberately excludes `Requester` (§7.3's narrowing), so leaving the CSRF-token endpoint on
  `AnyEmployee` would have made every cookie-authenticated, antiforgery-protected `POST` unreachable
  for a Requester — including `POST /api/requests` itself. `JobTrack.ExternalApiClient` (the
  first-party client proof, no `JobTrack.*` library reference) gained
  `GetEligibleHoldingAreasAsync`/`SubmitRequestAsync`/`GetMyRequestsAsync`, exercised against both
  providers by `ExternalApiClientProofTests`.
