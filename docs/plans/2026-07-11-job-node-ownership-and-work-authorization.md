# Job-node ownership, unassigned pool, and owner-gated work authorization

**Date:** 2026-07-11
**Status:** Implemented. All 9 stages are closed, `docs/ownership-model.md` is `accepted`, and
ADR 0031/0032 are recorded. Traceability: `TC-DB-HIER-006` through `-011` (schema, ancestor-owner
walk, pickup, concurrent-pickup race, reassignment/release, `OwnershipFilter`),
`TC-DB-SESSION-003`/`TC-APP-AUTHZ-002` (owner-gated work-session recording),
`TC-APP-AUTHZ-003`/`TC-WEB-HIER-005`/`-006` (pickup policy, web/API pickup), and the corresponding
external-API surface in `docs/traceability/test-catalogue.md`.
**Normative model:** [`../ownership-model.md`](../ownership-model.md) (read first; this plan does not
restate the rules, only how to build them).
**Closes with:** ADR 0031 (ownership / unassigned pool / pickup) and ADR 0032 (owner-gated work
recording; `WorkSessionAccessPolicy` breaking change).

## 1. Problem

Work-session authorization is gated only on self-vs-others
(`WorkSessionAccessPolicy.CanManage(roles, isOwnSession)`), so any authenticated Worker can record
their own time against **any** node in the system. The legacy `0..1` work-record cardinality made a
single worker implicit; the current many-`work_session`-per-leaf schema removed that guarantee
without replacing the control model. We make **node ownership** the control axis for both structure
and work, and add an explicit **unassigned pool + pickup** path so a worker can legitimately acquire
a node to work on.

The target rules — ownership states, the `controls` ancestor cascade, the four authorization
predicates, reassignment/release, and the worked examples — are specified in
[`../ownership-model.md`](../ownership-model.md) §2–§6 and are not repeated here.

## 2. Decisions taken (from design review)

1. **One direct owner, nullable.** `owner_user_id` becomes `NULL`-able; `NULL` = unassigned/pool. No
   multi-assignee table. Several people on one job = several child nodes each directly owns, or one
   controller records work sessions on behalf of several workers.
2. **Ancestor ownership cascades** for both structure and work (`controls`, model §3).
3. **Owner may assign to anyone**, including creating unassigned children and releasing owned nodes
   back to the pool (model §4.4) — relaxes spec `:303`.
4. **No worker self-service without control.** A Worker who controls nothing on a tree records no
   work there; they pick up an unassigned node or are given one (model §4.2).
5. **JobManager keeps hierarchy-wide authority** (unchanged); ownership gates the Worker role only.
6. **`NULL` is always publicly pickable**, ancestors ignored (model §2.1, §4.3). An owned ancestor's
   owner *also* controls a `NULL` descendant; the two coexist until claimed. Branch pickup is allowed
   and grants control over the picked branch's full subtree through the ordinary ancestor rule.

Assumptions still to confirm (model §8): pickup role scope (Worker+), release authority (=
`canManageStructure`), pickup granularity (any node, not leaves only). Proceed on these defaults;
revisit if the confirmations differ.

## 3. Architectural constraints

- **Layer order (CLAUDE.md / impl plan §1).** Defects are fixed in the earliest layer they belong to
  and that layer's gate re-passes before the next. This change originates at the **database
  contract** layer (nullable column + root guard), then the **reusable library** (domain policy +
  ports + client surface), then the **web/API** hosts. Do not patch the web layer to compensate for a
  missing library rule.
- **Dual-provider parity.** Every schema and enforcement change lands on **both** PostgreSQL and
  SQLite, tested by the shared contract suite first (TDD order per slice: shared contract →
  PostgreSQL → SQLite → provider race).
- **Pre-release schema editing (CLAUDE.md).** Nothing has shipped, so edit the existing
  `schema-versions/0004_job-node-and-priority.sql` files **in place**; do not add a forward-only
  `ALTER` script.
- **Public-API discipline.** `WorkSessionAccessPolicy.CanManage`'s signature change is a breaking
  change to a `JobTrack.Domain` public type past the library gate (§7.5). Review against
  `Framework_Design_Guidelines_Essentials.md` before and after; record the break in ADR 0032.
- **House style / TDD** as per CLAUDE.md: failing test first; immutable records; exhaustive switches;
  exceptions are the sole failure channel; Noda Time in the domain; every SQLite connection sets its
  pragmas.

## 4. Work breakdown (staged, each stage its own commit, TDD within)

Each stage: write the failing test(s) first, then the smallest correct implementation, then refactor.
Run the commit gate (`fast-test.sh --build` + targeted `--filter`) per commit; run the **full**
solution suite once at the end of Stage 9 and before the final commit.

### Stage 1 — Schema: nullable owner + root guard (database layer)

- **Tests first** (shared contract suite, then PG, then SQLite):
  - a non-root node may be inserted/updated with `owner_user_id = NULL`;
  - the **root** node may **not** have `owner_user_id = NULL` (insert and update both rejected);
  - the FK to `app_user` still holds for non-null owners;
  - existing `(owner_user_id, archived_at)` index still usable with NULLs (index-usage or plan test as
    already patterned in the schema suite).
- **Implementation:**
  - `database/postgresql/schema-versions/0004_job-node-and-priority.sql`: drop `NOT NULL` on
    `owner_user_id`; add a `CHECK`/partial mechanism so a root row (`parent_id IS NULL` /
    `kind = Root`) requires a non-null owner. Prefer expressing the root-owner guard alongside the
    existing single-root/permanent-root guards for locality.
  - `database/sqlite/schema-versions/0004_job-node-and-priority.sql`: mirror — nullable column plus a
    root-owner-non-null trigger consistent with the existing root guards.
- **Entity:** `src/JobTrack.Persistence.Shared/Entities/JobNodeEntity.cs` — `OwnerUserId` becomes
  `AppUserId?`. Adjust the EF configuration (required → optional) in the shared configuration.
- **Shared hierarchy primitive:** update `JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync` so
  `NULL` owners are skipped deliberately, either by filtering `owner_user_id IS NOT NULL` in SQL or
  by returning nullable IDs and filtering at the call site. Tests must pin that an unassigned node on
  the path grants nobody control and does not break a higher ancestor owner's control.
- Existing rows are all non-null today, so relaxing to nullable is backward-compatible; no data
  migration.

### Stage 2 — Public application boundary and read models (library layer)

- **Tests first** (`JobTrack.Application.Tests`, query-port contract tests, public API approval):
  - `CreateJobNodeRequest`, `EditJobNodeRequest`, `NewChildJobSpec`, `JobNodeResult`, and
    `JobNodeSummaryResult` all represent `OwnerUserId = null` intentionally;
  - browse/search/awaiting-progress queries can return unassigned nodes without throwing or
    dereferencing `.Value`;
  - callers can distinguish "no owner filter" from "only unassigned" in children/search/pool views;
  - `PublicAPI.Unshipped.txt` records the nullable signature changes.
- **Implementation:**
  - change command request/result DTOs that carry the node's direct owner to `AppUserId?`;
  - adjust `SnapshotJobNode`, audit serialization, browse/query projections, fake ports, and test
    support builders to handle null owners deliberately;
  - replace nullable-owner filters whose current `null` means "no filter" with an explicit filter
    shape, for example `OwnershipFilter.All`, `OwnershipFilter.OwnedBy(AppUserId)`, and
    `OwnershipFilter.Unassigned`. Keep the exact type name aligned with existing API style.

### Stage 3 — Domain policy (library layer)

- **Tests first** (`JobTrack.Domain.Tests`):
  - `WorkSessionAccessPolicy.CanManage(roles, actorControlsNode)`: Admin/JobManager true regardless;
    Worker true iff `actorControlsNode`; no role → false; a Worker recording for another user is
    allowed when they control the node (the "own session" notion is gone).
  - a new pickup predicate — `JobPickupPolicy.CanPickUp(roles, ownerIsNull)` (or a method on the
    existing node policy): true iff `ownerIsNull` and the actor holds Worker/JobManager/Administrator;
    false when already owned; false for read-only roles.
  - `JobNodeAccessPolicy.CanManage` unchanged in shape; add tests pinning that a `NULL` owner in the
    ancestor set grants **nobody** control (the `controls` skip-NULL rule) — this is enforced by the
    caller computing the owner set, so the test belongs where that set is built (Stage 1/4) as well.
- **Implementation:**
  - Change `WorkSessionAccessPolicy.CanManage` signature from `(roles, isOwnSession)` to
    `(roles, actorControlsNode)`; update XML docs to cite the new model and ADR 0032.
  - Add the pickup policy type/method. Keep it pure (roles + booleans in, bool out), matching the
    house pattern of the other policies.

### Stage 4 — Work-session command ports: owner-gated authorization (library layer)

- **Tests first** (provider contract suites — PG then SQLite):
  - owner records a session for **another** user → allowed;
  - non-owner Worker records **their own** session on an owned node → **denied**;
  - non-owner Worker on an **unassigned** node → denied (must pick up first);
  - Worker who owns an **ancestor** records on a descendant leaf → allowed;
  - Admin/JobManager record on any node incl. unassigned → allowed;
  - the change is applied identically to `StartSession`, `FinishSession`, `CorrectSession`.
- **Implementation:**
  - `src/JobTrack.Persistence.PostgreSql/PostgreSqlWorkSessionCommandPort.cs`: replace the
    `actorId == workedByUserId` computation in `AuthorizeOrThrowAsync` with a `controls(actor, leaf)`
    computation — reuse the ancestor-owner-set walk already implemented for the job-node port
    (`GetAncestorOwnerIdsAsync`), including the node's own owner, skipping NULLs, then call
    `WorkSessionAccessPolicy.CanManage(roles, controls)`.
  - Mirror in the SQLite work-session command port.
  - Audit events must continue to record `request.Context.Actor` as the actor and
    `worked_by_user_id` as the worker; on-behalf-of recording must not blur those fields.
  - Keep the existing readiness (`IsReadyAsync`) and already-active checks unchanged and ordered
    after the authorization check.

### Stage 5 — Pickup command (library layer)

- **Tests first** (provider contract + race):
  - claiming an unassigned node sets `owner = actor` and removes it from the pool;
  - claiming an unassigned **branch** grants the claimant control over descendants through the
    ancestor rule, including already-owned descendants;
  - claiming an already-owned node throws `already-claimed`;
  - **concurrent** double-pickup: exactly one claimant wins, the other gets `already-claimed`
    (provider race test, both engines) — conditional `WHERE owner_user_id IS NULL` update;
  - pickup does **not** require readiness;
  - a read-only role (Auditor) cannot pick up.
- **Implementation:**
  - `IJobCommands.PickUpAsync(nodeId, CommandContext)` in `JobTrack.Application`, delegating to a new
    `IJobNodeCommandPort.PickUpAsync`.
  - PostgreSQL/SQLite ports: conditional update `SET owner_user_id = @actor WHERE id = @id AND
    owner_user_id IS NULL`; zero rows affected → throw the `already-claimed` invariant
    (`InvariantViolationException`, new `ConstraintId "job-node-already-claimed"`). Authorize with the
    new pickup policy before the write; rely on the conditional update as the concurrency backstop.
  - Write an audit event with before `owner_user_id = NULL`, after `owner_user_id = actor`, and the
    acting user from `CommandContext`.
  - Wire through `IJobTrackClient`.

### Stage 6 — Reassignment / release to pool (library layer)

- **Tests first:**
  - controlling owner reassigns a node to any user → allowed;
  - ancestor owner reassigns/releases a descendant directly owned by another user → allowed and
    audited;
  - controlling owner sets owner to `NULL` (release) → allowed;
  - releasing/renulling the **root** → rejected (schema guard from Stage 1 + application-side check
    for a clear error);
  - a non-controlling Worker cannot reassign.
- **Implementation:**
  - The existing `EditAsync` path already writes `OwnerUserId` from the request and is gated by
    `canManageStructure`; make its acceptance of any target owner (incl. `NULL`) **deliberate and
    tested** rather than incidental. Add the root-owner-non-null application guard with a specific
    `ConstraintId` so the error is actionable rather than surfacing only as a DB constraint.
  - Confirm child creation (`AddBranch`/`AddLeaf`) accepts `OwnerUserId = null` (unassigned child) and
    any explicit owner, gated by control of the parent.
  - Ensure audit before/after payloads serialize nullable `owner_user_id` without throwing.

### Stage 7 — Web host (Razor Pages)

- **Tests first** (`JobTrack.Web.EndToEndTests` / page tests):
  - unassigned nodes render as pickable with a "Pick up" action; owned nodes do not;
  - pool filters show unassigned nodes without conflating them with "all owners";
  - an owner's work-entry form exposes a `worked_by` selector (record on behalf of another user);
  - a non-owner Worker sees neither work-entry nor (for owned nodes) pickup;
  - axe/WCAG-AA scan re-run after any UI/colour change (CLAUDE.md design-language rule).
- **Implementation:** Browse/Work pages — pickup control, on-behalf-of `worked_by` selector, and
  pool visibility. Replace raw owner-id rendering paths that assume `.Value` with a deliberate
  unassigned display state. Compose existing Console design-language primitives; no bespoke CSS.

### Stage 8 — External HTTP API

- **Tests first** (`JobTrack.PublicApi.Tests` / API integration): nullable `ownerUserId` in create,
  edit, and result contracts; explicit unassigned filter/pool route semantics; pickup endpoint;
  session endpoints reflect owner-gated authorization; OpenAPI route-set test updated; unauthorized
  cases return the documented problem responses. Keep the first-party client proof
  (`samples/JobTrack.ExternalApiClient`) talking only to the HTTP/OpenAPI contract — no project
  reference added.
- **Implementation:** add the pickup route and adjust session routes; regenerate/extend the OpenAPI
  surface; update the sample client’s plain-JSON model if the contract shape changes.

### Stage 9 — Docs, ADRs, spec, traceability

- Author **ADR 0031** (ownership states, unassigned pool, pickup, cascade) and **ADR 0032**
  (owner-gated work recording; `WorkSessionAccessPolicy` breaking change + FDG review notes).
- Flip `docs/ownership-model.md` status from *proposed* to *accepted* and drop the "supersedes"
  qualifier once ADRs land.
- Update `jobtrack_spec_codex.md`: `:85` (direct owner optional; `NULL` = unassigned; ownership not
  evidence of work), `:296` (Worker work-session wording → owner-gated, records for any user, may
  claim unassigned), `:303` (relaxed reassignment + pool/pickup; ancestor owner may reassign
  descendant direct owner; keep "never inferred from posting/current worker/client claims"). Mirror
  any needed detail in `jobtrack_spec_claude.md` (Codex wins on conflict). Update
  `database-entities.md:85` (`OwnerUserId` now `0..1`).
- Add test IDs to `docs/traceability/test-catalogue.md` for each new invariant (nullable owner, root
  guard, owner-gated recording, on-behalf-of recording, pickup claim, concurrent pickup race,
  branch pickup cascade, reassignment/release, root-release rejection, unassigned query filter).
- Re-pass the affected phase gates in order: database (§6.7), then library (§7.5), then web (§8.7) —
  fixing at the earliest layer, not the latest.

## 5. Explicitly out of scope

- Multi-assignee nodes (a join table of several workers per node) — rejected in design (decision 2.1).
- Any change to the `work_session` overlap constraints (schema 0007) — they remain keyed on
  `(worked_by_user_id, leaf_work_id)` and are orthogonal to ownership.
- Retroactive ownership backfill/data migration — existing rows are all non-null; `NULL` is a
  forward-only, intentional state.

## 6. Risks / watch-list

- **Breaking `WorkSessionAccessPolicy` signature** ripples to every caller and its unit tests; land
  Stage 3+4 together conceptually even if committed separately, so the library never sits in a
  half-migrated state across a gate.
- **Ancestor-owner walk reuse:** the work-session port must compute the *same* control set as the
  job-node port (own owner + ancestors, NULLs skipped). Extract a shared helper rather than
  duplicating the walk, to avoid the two ports drifting.
- **Nullable owner is a boundary change, not just a schema change:** application DTOs, public API
  models, Razor page models, query filters, audit snapshots, fake ports, and test builders currently
  assume a non-null owner in several places. Land the nullable public boundary before relying on the
  new database state.
- **Owner filter ambiguity:** `AppUserId?` cannot express both "no owner filter" and "only
  unassigned". Use an explicit filter value object or enum-backed request field before adding pool
  views, otherwise the UI/API will have no stable way to ask for the pool.
- **Root-owner guard** must be enforced at the DB (authoritative) *and* surfaced with a clear
  application-side error; relying on the raw DB constraint message alone is a poor operator
  experience.
- **Pickup race** is the one genuinely concurrent new path; the conditional `WHERE owner IS NULL`
  update is the correctness mechanism — the provider race tests are not optional.
