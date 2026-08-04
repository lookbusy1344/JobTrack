# ADR 0058: Auto-acknowledge a requester request on first work or outcome

**Status:** Accepted
**Amends:** ADR 0034 (`Accepted` is a persisted staff action) — narrows, does not reverse, its
"never inferred" rule.

## Context

ADR 0034 made `job_request.acknowledged_at` an explicit, staff-only, one-shot action
(`AcknowledgeAsync`), deliberately rejecting inferring it from other staff activity: "inferring it
from assignment or a move would conflate 'staff looked at it' with 'staff did something
structural', producing a false `Accepted` for a request an owner glanced at and left untouched."

That objection targets *structural, non-committal* staff actions — assignment, a holding-area move
— which a staff member can perform without ever having engaged with the request's substance. It
does not address the case this ADR closes: a leaf under the request's subtree actually starting
work, or reaching a final achievement, while the request is still unacknowledged. Both are
unambiguous evidence staff engaged with the request; a requester should never see `Submitted` next
to a job that is already in progress, paused, done, or cancelled.

## Decision

- **First transition into `InProgress`** on any leaf under an unacknowledged request's subtree
  (`StartWorkAsync`, `ReopenAndStartWorkAsync`) auto-acknowledges the request.
- **First transition into a terminal achievement** (`Success`/`Cancelled`/`Unsuccessful`, i.e.
  `AchievementTransitions.IsCompletedState`) on any leaf under an unacknowledged request's subtree
  (`SetAchievementAsync`, `CompleteLeafAsync`) auto-acknowledges the request — including the direct
  `Waiting -> Cancelled`/`Unsuccessful` path, which never passes through `InProgress`.
- Auto-acknowledgement sets `acknowledged_at`/`acknowledged_by_user_id` exactly as `AcknowledgeAsync`
  does, using the triggering operation's own `request.Context.Actor`, in the **same transaction and
  commit** as the triggering write — never a second round trip.
- It is a **silent, idempotent no-op** if the request is already acknowledged
  (`acknowledged_at is not null`) — no `InvariantViolationException`, unlike explicit
  `AcknowledgeAsync`, since this path is not the requester of the action.
- Audited as `"auto-acknowledge-request"` (distinct from `"acknowledge-request"`), same shape as the
  explicit audit event, so the audit trail records *why* acknowledgement happened.
- Finding the owning request from an arbitrary leaf walks ancestors
  (`JobNodeHierarchyQueries.GetNearestRequestAnchorIdAsync`) to the nearest `job_request` anchor,
  since `job_request` is keyed by its anchor node and a leaf may be several decompositions below it.
  The walk is ordered by distance and takes the closest anchor: schema version 0020 does not forbid
  one request anchoring inside another's subtree, so "nearest" is a decision, not an accident of row
  order.
- The mutation is **one conditional `UPDATE ... WHERE acknowledged_at IS NULL`** (EF
  `ExecuteUpdateAsync` on the anchor row, inside the triggering command's own transaction), not a
  tracked read/check/mutate. A tracked mutation carries `job_request.row_version` as an optimistic
  concurrency token, so two transactions starting work on *different* leaves under one unacknowledged
  request would both read the same version and the loser's `SaveChangesAsync` would raise
  `DbUpdateConcurrencyException` — rolling back a leaf command that never conflicted and reporting it
  to the user as a leaf/session concurrency conflict, contradicting both the "silent, idempotent
  no-op" rule above and the Work-page rule that a concurrency message must mean the relevant version
  actually moved. With the conditional update the loser blocks on the winner's row lock, re-evaluates
  the predicate after that commit, matches zero rows, and continues; the audit event is queued only
  by the transaction whose statement actually affected the row, so it can never be written twice.
- Applies identically to both providers (PostgreSQL, SQLite) and to **every** command-port path that
  moves a leaf into one of those states, not to an enumerated list of methods:
  `StartWorkAsync`/`ReopenAndStartWorkAsync` (`*WorkSessionCommandPort`),
  `SetAchievementAsync`/`CompleteLeafAsync` (`*AchievementCommandPort`,
  `*WorkSessionCommandPort.CompleteLeafAsync`), `AddChildAsync`'s create-and-begin-work composite,
  and `ImportSubtreeAsync` replaying an imported leaf's recorded final achievement. The import keeps
  its own single `import-leaf-work` audit event rather than gaining a `set-achievement` one; only the
  acknowledgement is shared, because a requester must not see `Submitted` beside a job the import
  already recorded as in progress or finished.
- **The transition itself is the shared unit**, not the acknowledgement call.
  `Persistence.Shared.LeafAchievementTransition.ApplyAsync` owns the mutation, the concurrency-token
  bump, the `set-achievement` audit event, and this acknowledgement together
  (`ApplyImportedAsync` is the import's variant, without the token bump or the audit event); no other
  code in either provider reassigns a tracked `LeafWorkEntity.Achievement`, which
  `LeafAchievementTransitionArchitectureTests` enforces. This replaced a per-call-site convention
  after `AddChildAsync`'s create-and-begin-work composite reproduced the transition locally and
  silently dropped the acknowledgement — a list of sites that must remember cannot prevent the next
  composite doing the same.
- **Implemented once, in `JobTrack.Persistence.Shared`**, as a single helper invoked from inside
  each of the 8 call sites' already-open `DbContext`/transaction — never duplicated per provider and
  never split into a second `SaveChangesAsync`/commit outside the triggering write. Pushing this to
  `JobTrack.Application` instead was considered and rejected: by the time an `IWorkCommands`/
  `IAchievementCommands` call returns, the port's transaction has already committed, so a
  second write there would split one compound change across two commits — the exact pattern
  the "Compound writes are single ACID transactions" house-style rule forbids.

## Rationale

- Starting work or recording an outcome is a *substantive* engagement with the request's own leaf,
  not a structural housekeeping action — the distinction ADR 0034 actually drew, not a blanket ban
  on inference.
- A silent no-op (rather than throwing) keeps every triggering command's contract unchanged for
  callers that never think about acknowledgement; only the persisted side effect and its audit trail
  are new.
- A distinct audit reason (`auto-acknowledge-request` vs `acknowledge-request`) preserves the
  existing invariant that the audit log answers "who did this and why" without conflating an
  implicit side effect with a deliberate staff action.

## Consequences

- `RequesterStatusCalculator`'s existing precedence is unaffected — `InProgress`/`Waiting`/
  `Completed`/`Cancelled` already outrank the `Accepted`/`Submitted` split, so this change is only
  observable when a request later drops back to needing the `acknowledged` tiebreaker (e.g. after a
  `Waiting -> Cancelled` leaf makes acknowledgement newly load-bearing for that leaf's own history,
  and for any UI that surfaces `acknowledged_at` directly rather than only the derived status).
- New contract tests are added per provider for: auto-ack on `StartWorkAsync`, on
  `SetAchievementAsync` (the direct `Waiting -> Cancelled`/`Unsuccessful` origin), and on
  `CompleteLeafAsync`; plus a no-op case when already acknowledged, a nearest-enclosing-anchor case
  covering nested anchors, per-provider races proving two simultaneous first-work/terminal-outcome
  commands on different leaves under one request both succeed with exactly one acknowledgement, and
  an audit-event assertion for `"auto-acknowledge-request"`.
- Schema version 0020's `job_request_no_reacknowledge` trigger makes acknowledgment fully immutable
  once set — not only rejecting a second explicit `acknowledge-request`, but forbidding any change
  back to unacknowledged. Combined with `CompleteLeafAsync`/`SetAchievementAsync` already
  auto-acknowledging on the transition into a terminal achievement, this means
  `ReopenAndStartWorkAsync`'s own hook can never observe an unacknowledged request in practice — its
  test instead proves that call is a harmless no-op (no second `auto-acknowledge-request` audit
  event), not a first acknowledgement.
- `docs/plans/2026-07-11-client-requester-intake-plan.md` gains a note that ADR 0058 narrows ADR
  0034's "never inferred" rule for these two trigger points.
