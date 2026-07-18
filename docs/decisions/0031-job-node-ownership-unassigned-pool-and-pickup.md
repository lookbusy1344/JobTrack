# ADR 0031: Job-node ownership states, the unassigned pool, and pickup

**Status:** Accepted
**Closes:** `docs/ownership-model.md` §2–§6 (ownership states, ancestor cascade, structural/pickup/
reassignment predicates); `docs/plans/2026-07-11-job-node-ownership-and-work-authorization.md`
Stages 1, 2, 5, 6; supersedes `jobtrack_spec_codex.md` `:85` and `:303` to the extent described
below.

## Context

`job_node.owner_user_id` was `NOT NULL`: every node, including a newly created one, had to be
assigned a direct owner at creation. There was no way to express "this job exists but nobody has
claimed it yet" — a job manager posting work for a team had to either guess an owner up front or
assign themselves as a placeholder, and there was no controlled path for a worker to legitimately
acquire a node to work on beyond being directly assigned it by someone with structural authority.

Reassignment was also narrower than day-to-day use needed: spec `:303` restricted changing a node's
owner to Admin/JobManager, so a worker who structurally controlled a subtree (via ancestor
ownership) could not hand a piece of it to a teammate or release it back without escalating to a
manager.

## Decision

`job_node.owner_user_id` becomes nullable everywhere except the permanent root, whose owner is set
at installation bootstrap and must always be non-null (schema-level `CHECK`/trigger guard, plus an
application-side `"job-node-root-owner-required"` invariant surfaced before the database is ever
asked). `NULL` is a first-class, intentional state — the **unassigned pool** — not a data defect to
backfill.

**Control cascades down the tree.** An actor **controls** a node if they directly own it or any of
its ancestors; a `NULL`-owned node on the path contributes nobody (skipped, not "owned by no one in
particular"). This is the single predicate `canManageStructure` (job-node structural commands) is
built from — unchanged in shape from the prior `JobNodeAccessPolicy.CanManage`, refined only so the
ancestor-owner walk it consumes (`GetAncestorOwnerIdsAsync`) skips `NULL` owners rather than
treating them as a match.

**Pickup is a new, distinct predicate**, deliberately *not* gated by ancestor control: `canPickUp`
is true iff the node's own `owner_user_id IS NULL` and the actor holds Worker, JobManager, or
Administrator — even an ancestor's owner does not block a claimant, and even an Administrator cannot
pick up an already-owned node (pickup is about the pool, not a role bypass). A successful pickup is
a conditional `UPDATE ... WHERE owner_user_id IS NULL`; the correctness of the concurrent-claim race
rests on that conditional write, not a lock — a losing claimant sees zero rows affected and gets
`InvariantViolationException("job-node-already-claimed")`, never a silently overwritten winner.
Claiming a branch grants control over its whole subtree through the ordinary ancestor rule,
including descendants that are themselves unassigned or already owned by someone else.

**Reassignment is relaxed.** Anyone for whom `canManageStructure` is true — including a Worker who
controls a subtree only through ancestor ownership, not Admin/JobManager exclusively — may set a
node's owner to any user or to `NULL` (release to the pool). Because control cascades, an ancestor
owner may reassign or release a descendant even when that descendant has a different direct owner.
This relaxes spec `:303`; the permanent root may never be released to `NULL`, enforced the same way
creation of a null root owner would be.

Ownership remains **never inferred** from the posting user, the current worker, or any
client-supplied claim (spec `:85`'s existing rule, unaffected) — it is set explicitly at creation,
changed only by explicit reassignment, and acquired from the pool only by explicit pickup.

## Rationale

- A separate pickup predicate (rather than folding pickup into `canManageStructure`) keeps "may I
  restructure this subtree" and "may I claim this specific unclaimed node" as the different
  questions they are — an ancestor owner's structural authority over their subtree says nothing
  about whether a stranger may claim an unrelated unassigned leaf elsewhere in the tree, and vice
  versa a claimant of a pool leaf gains no say over its siblings until they actually claim them too.
- Gating pickup on the node's own owner only (not ancestor control) matches the plain-language
  meaning of "unassigned" — a job under an owned branch that the branch owner deliberately left
  unassigned is exactly as claimable as one under an unowned branch; ancestor ownership is a fact
  about control, not about whether the pool entry itself has been claimed.
- The conditional-update-as-concurrency-mechanism design (vs. an advisory lock, cf. ADR 0012's
  locked operations) was chosen because pickup has no natural serialization point analogous to
  `move_job_node`'s cycle check or the prerequisite-graph writes — the invariant being protected
  ("exactly one claimant wins") is expressible directly as a `WHERE` clause, so introducing a lock
  domain for it would be unjustified complexity.
- Relaxing reassignment to any controlling owner, not just Admin/JobManager, follows the same logic
  that already grants a controlling Worker full structural authority over their subtree (spec
  §7.3) — reassigning or releasing a node they control is not a bigger act of authority than
  archiving or moving it, both of which were already permitted.

## Consequences

- `JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync` filters `owner_user_id IS NOT NULL` in its SQL,
  reused identically by both the job-node and work-session command ports (ADR 0032) so the two
  never compute divergent control sets.
- `IJobCommands.PickUpAsync`/`IJobNodeCommandPort.PickUpAsync` and `JobPickupPolicy.CanPickUp` are
  new public surface; `WorkSessionAccessPolicy` review under this ADR's sibling, ADR 0032.
- Query/read models and public DTOs (`JobNodeResult`, `JobNodeSummaryResult`, `CreateJobNodeRequest`,
  `EditJobNodeRequest`, `NewChildJobSpec`) carry `AppUserId? OwnerUserId` deliberately; owner filters
  distinguish "no filter" (`OwnershipFilter.All`), "only unassigned" (`OwnershipFilter.Unassigned`),
  and "owned by X" (`OwnershipFilter.OwnedBy`) — a bare nullable id cannot express the first two
  simultaneously.
- The external HTTP API and Razor Pages web host both expose pickup and an unassigned-pool filter
  (`unassignedOnly`); structural mutation (create/edit/move/archive) otherwise remains
  Razor-Pages/AdminCli-only per ADR 0030, unaffected by this ADR.
- `jobtrack_spec_codex.md` `:85` and `:303`, and `database-entities.md`'s `OwnerUserId` cardinality,
  are updated to reflect the nullable, pool-enabled model this ADR describes.
