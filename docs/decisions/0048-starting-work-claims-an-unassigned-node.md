# ADR 0048: Starting a work session claims an unassigned node

**Status:** Accepted
**Closes:** an open question raised against ADR 0031/0032; amends `docs/ownership-model.md` §4.2/§4.3.
Amends (does not reverse) ADR 0031's "pickup is a distinct, deliberate action" framing and ADR
0032's "an unassigned node is not directly workable by a plain Worker" consequence, both only for
the session-start moment specifically.

## Context

ADR 0031 introduced the unassigned pool and a separate, explicit `PickUpAsync` claim action. ADR
0032 then gated work-session recording on node control (`canRecordWork`), with the consequence that
a plain Worker could not start a session on an unassigned node at all — `controls` is false for
everyone until someone claims it — so the only path to working a pool node was two round trips:
pick it up, then separately start a session on it.

That two-step requirement has no invariant behind it. A `WorkSession` row names exactly who is doing
the work (`worked_by_user_id`); a node with an active session but `owner_user_id IS NULL` asserts,
simultaneously, that specific work is happening and that nobody is responsible for the node it's
happening on. That is a contradiction, not a valid intermediate state — the same category of
unnecessary friction ADR 0038 already removed for the `Waiting -> InProgress` achievement step: the
next action was already obvious and mandatory, so require it explicitly.

## Decision

`StartSessionAsync`, `StartWorkAsync`, and `ReopenAndStartWorkAsync` (both persistence providers)
each gain an auto-claim step, run inside the same transaction as the rest of the call, immediately
before the existing `canRecordWork` authorization check:

1. If the target node's `owner_user_id` is not `NULL`, this step is a no-op — behavior is completely
   unchanged for every already-owned node.
2. If it is `NULL`, the actor's roles are checked against the identical eligibility test explicit
   pickup already uses: `JobPickupPolicy.CanPickUp(actorRoles, true)` (Worker, JobManager, or
   Administrator). An actor who fails this check is left for the existing `canRecordWork` denial to
   reject, unchanged.
3. An eligible actor claims the node **for `request.WorkedByUserId`**, not for the actor — the same
   conditional, race-safe write `PickUpAsync` uses (`UPDATE job_node SET owner_user_id = ...
   WHERE id = ... AND owner_user_id IS NULL`), so a losing concurrent claimant sees zero rows
   affected and gets the same `InvariantViolationException("job-node-already-claimed")` explicit
   pickup already throws — inside this call's own transaction, so a losing attempt leaves neither a
   claim nor a session.
4. A successful claim writes the same `pick-up-job-node` audit event `PickUpAsync` writes, with a
   fixed distinguishing reason, `"Automatically claimed on session start"` (mirroring ADR 0038's
   `AutoAdvanceReason` pattern), so an audit reader can tell an implicit claim from an explicit one.

`canRecordWork` then runs exactly as before, against the node's now-current ownership. Practical
consequences:

- A plain Worker starting their own first session on an unassigned leaf now succeeds directly — the
  leaf is claimed for them as part of the same call, no separate `PickUpAsync` round trip required.
- Starting a session **for** a different worker on an unassigned node claims it for that worker, not
  for the acting caller, consistent with ownership already meaning "who does the work," not "who
  submitted the request" (ownership-model.md §4.2's existing on-behalf-of recording rule).
- Administrator/JobManager, who could already start a session on an unassigned node without
  controlling it, no longer leave it unassigned afterward — the node is claimed as a side effect even
  though their authority never depended on `controls`.
- A Worker starting a session **for** someone else on a node nobody controls is unaffected by this
  ADR and remains denied exactly as before: the node claims for the other worker, the acting Worker
  still doesn't control it, and `canRecordWork` still refuses them.

Explicit `PickUpAsync` is untouched and remains available — this is additive convenience for the
common case (the first action taken on a pool node is, in practice, always "start working it"), not
a replacement. A manager can still pick up a node without starting work on it immediately, e.g. to
reserve it or reassign it later.

## Rationale

- Reusing `JobPickupPolicy.CanPickUp` and the identical conditional-UPDATE mechanism, rather than
  inventing a parallel claim path, keeps "who may bring a node out of the pool" answered in exactly
  one place, per ADR 0031's own rationale for why pickup is a distinct predicate to begin with —
  this ADR changes *when* that predicate fires, not what it means.
- Claiming for `WorkedByUserId` rather than the acting caller keeps ownership meaning what it already
  means everywhere else in this model, rather than introducing a second, divergent notion of "who
  gets the node" depending on which command happened to touch it first.
- Running the claim inside the same transaction as the session start (rather than as a prior,
  separate call) is what actually removes the friction: a caller gets one round trip, one atomic
  outcome, and the same race-safety guarantee explicit pickup already had, per the project's
  compound-write-is-one-transaction convention.

## Consequences

- `PostgreSqlWorkSessionCommandPort`/`SqliteWorkSessionCommandPort`'s `StartSessionAsync`,
  `StartWorkAsync`, and `ReopenAndStartWorkAsync` each gain a private `AutoClaimUnassignedNodeAsync`
  (or equivalently named) helper, called before their existing `AuthorizeOrThrowAsync`/
  `AuthorizeReopenAndStartOrThrowAsync` call.
- `docs/ownership-model.md` §4.2 is updated: "an unassigned node is not directly workable by a plain
  Worker" no longer holds unconditionally — starting a session is now itself sufficient to claim it.
  §4.3 gains a cross-reference noting session-start as a second entry point into the same claim
  mechanism.
- `FinishSessionAsync`/`CorrectSessionAsync` are unaffected: by the time either runs, an active
  session already implies the node was claimed at start (or was already owned).
- The contract test `A_worker_cannot_start_a_session_on_an_unassigned_leaf`
  (`WorkSessionCommandPortContractTestsBase`) is replaced by a test asserting the opposite: a plain
  Worker starting a session on an unassigned leaf succeeds and the node ends up owned by
  `WorkedByUserId`. A new race test mirrors `PickUpAsync`'s own concurrent-claim coverage for the
  start-session path.
