# ADR 0045: Unified leaf completion and reopen-and-start authority

**Status:** Accepted
**Closes:** `docs/plans/2026-07-22-unified-leaf-workflow-plan.md` Stage 0.
**Amends:** ADR 0001 (achievement states and reopening authority), ADR 0032 (owner-gated work-session
authorization), ADR 0038 (auto-advance on session start), ADR 0044 (closed-leaf session creation and
closure serialization).

## Context

ADR 0001 gates every reopen behind Job Manager/Administrator authority. ADR 0038 auto-advances
`Waiting -> InProgress` on session start but leaves every other transition manual. ADR 0044 makes a
terminal or archived leaf reject new sessions and makes a terminal transition reject an active
session, but neither ADR nor any application command combines "finish the active session(s)" with
"record the outcome," or "reopen" with "start the next session," into one operation. The result is
operationally correct but requires the browser to walk the user through several independent pages and
commands to do what is, in practice, one decision: pause, or finish and mark done; reopen, or reopen
and get back to work.

The unified-leaf-workflow plan's product owner confirmed the semantics below on 2026-07-22. This ADR
records them as the amendment each existing ADR needs before the plan's Stage 1 (public contracts)
and later stages cite them as settled.

## Decision

### 1. Two new atomic composites, additive to the existing primitives

`StartWorkAsync`, `FinishSessionAsync`, `SetAchievementAsync`, and `CorrectSessionAsync` are
unchanged in meaning and remain independently callable (ADR 0001, 0038, 0044 all stay in force for
these primitives). Two new composites are added to `IWorkCommands` and the corresponding persistence
port, each one ACID transaction, reusing ADR 0044's per-leaf closure lock:

- **`CompleteLeafAsync`** — finishes an exact, caller-confirmed set of active sessions (zero, one, or
  many) at one captured instant and transitions the leaf `InProgress -> Success` with a structured
  system reason, in one commit. It never derives `Success` from elapsed time or session count, and it
  never touches `Cancelled`/`Unsuccessful` — those remain manual, single-purpose `SetAchievementAsync`
  calls behind ADR 0044's existing "no active session" gate.
- **`ReopenAndStartWorkAsync`** — transitions a terminal leaf `terminal -> Waiting` with a
  caller-supplied reason, applies ADR 0038's existing `Waiting -> InProgress` auto-advance, and starts
  one session for the target worker, in one commit. It is exactly ADR 0001's reopen followed by ADR
  0038's start-work composite, made atomic and, per §2 below, made available to a wider actor set than
  ADR 0001 originally granted.

Both composites revalidate readiness, authorization, optimistic concurrency (leaf version and, for
`CompleteLeafAsync`, each affected session's version), and ADR 0044's closed-leaf predicate inside the
transaction — never trusting what an earlier read handed the browser. A validation failure leaves the
prior committed state completely unchanged; neither composite is a UI-level wrapper around two
separately committing calls.

### 2. Reopening authority (amends ADR 0001 §"Reopening authority")

ADR 0001 restricted every reopen to Job Manager/Administrator. That restriction stands for
**`ReopenWithoutStartingAsync`-shaped elevated correction** (reopening without also starting a
session) but no longer describes the only path back to `Waiting`. `ReopenAndStartWorkAsync` (§1) uses
a wider authority test:

Reopening a terminal leaf through the composite is permitted when the actor is an enabled operational
employee and at least one of the following holds:

1. the actor recorded any previous session on this leaf (prior participation);
2. the actor controls the leaf through direct or ancestor node ownership (ADR 0031's `controls`
   predicate, the same one ADR 0032 already reuses); or
3. the actor is a Job Manager or Administrator.

A prior participant (authority 1 only, no node control) may use the composite to reopen and start a
session **for themselves only** — they may not name a different target worker. A controlling owner,
Job Manager, or Administrator (authority 2 or 3) may select any eligible target worker, exactly as
ADR 0032 already allows for starting a session generally. Historical participation never grants the
right to start *for someone else*; it only grants the right to get the leaf itself moving again.

Disabled accounts, `Requester`-only accounts, and employees holding no operational workflow role
acquire none of this authority merely by appearing in historical session data. `Requester` remains
excluded from every job-workflow write, per the existing role table (spec §7.3) — this ADR does not
touch that boundary.

**`ReopenWithoutStartingAsync`** (the elevated, session-free correction path listed in the plan's
§5.5 "advanced achievement actions" — administrative correction without inventing evidence that work
occurred) keeps ADR 0001's original Job Manager/Administrator-only restriction unchanged. The widened
authority in this section applies only to the composite that also starts a session, because starting
work is the concrete, auditable act (a new session row, a specific worker, a specific instant) that
justifies extending trust to a prior participant or controlling owner; reopening in isolation, with no
session following it, has no comparable concrete anchor and stays elevated-only.

Reopening a `Success` leaf still re-evaluates normal readiness for its dependents under ADR 0001 —
this ADR does not add a bypass. The application layer surfaces the current dependent impact before
mutation; the command re-evaluates readiness for dependents inside the transaction regardless of what
was shown.

### 3. Completion is `Success`-only, and is never implicit (amends ADR 0044's non-goals, no rule change)

`CompleteLeafAsync` records `InProgress -> Success` exclusively. `Cancelled` and `Unsuccessful` remain
distinct, manual `SetAchievementAsync` calls, unaffected by this ADR, and ADR 0044's existing rule
that a terminal transition is rejected while any session is active still governs them directly — they
gain no atomic "finish-all-and-close" composite here. Ordinary `FinishSessionAsync` also gains no
implicit meaning: finishing a session never implies success, and the plan's UI routes every "end
session" action through an explicit choice between pausing (finish only) and completing (the new
composite) rather than overloading the existing finish action.

`CompleteLeafAsync` accepts a caller-confirmed active-session set of zero, one, or many sessions:

- **Zero** active sessions is valid only from `InProgress` (a previously paused leaf resumed to
  readiness by the achievement graph, with no clock currently running) — `Waiting -> Success` remains
  prohibited by ADR 0001's transition graph unchanged; a leaf that never started is cancelled or
  marked unsuccessful, never silently recorded as succeeded.
- **One** active session is the common case: it finishes at the captured instant and the leaf
  transitions to `Success` in the same commit.
- **Several** active sessions (concurrent workers, permitted since ADR 0041/0044 never collapse them)
  all finish at exactly the same captured instant, atomically, only after the caller has reviewed and
  confirmed the specific set. The command re-verifies that the active-session ids and versions it is
  about to finish are exactly the set confirmed — a session that started concurrently after the
  caller's read must produce a conflict, never be silently swept into (or silently excluded from) the
  finish.

### 4. Structured completion reason (amends nothing normatively; records UX/audit convention)

`CompleteLeafAsync`'s audit reason is a fixed structured system string, e.g. `"Completed from the leaf
work page"` — the same pattern ADR 0038 already established for its own auto-advance reason — with an
optional free-text completion note carried alongside it in progressive disclosure, never a mandatory
second form. `ReopenAndStartWorkAsync` continues to require a genuine user-supplied reason for the
`terminal -> Waiting` half, consistent with ADR 0001's existing reopening-reason requirement; the
application layer may offer quick-choice reason text, but the persisted audit value is always the
resolved text the user confirmed, never a UI option code.

### 5. Who may end whose session (amends ADR 0032 narrowly)

ADR 0032 gates `FinishSessionAsync` (via `WorkSessionAccessPolicy.CanManage`) on `controls(actor,
node)`, with the Administrator/JobManager bypass unconditional. This ADR adds one narrow exception:

**The worker named on an active session may always finish that specific session themselves** — i.e.
`CanManage` additionally admits `actorId == session.WorkedByUserId`, regardless of whether that actor
still controls the node the session belongs to at finish time. Ownership can change after a session
starts (reassignment, release to the pool, pickup by someone else); without this exception a worker
who started a session while controlling the node could be left unable to stop their own clock purely
because control moved elsewhere in the meantime. This exception governs "pause work" (finish, no
achievement change) only — it grants no completion authority.

Everything else about `CanManage` is unchanged:

- a direct/ancestor controlling owner, Job Manager, or Administrator may still finish **any** selected
  active session on a leaf they control, exactly as ADR 0032 already permits;
- `CompleteLeafAsync` (§1, §3) requires the same authority `SetAchievementAsync` already requires for
  the terminal transition — controlling owner, Job Manager, or Administrator — because it ends every
  affected active session *and* changes achievement; the new self-finish exception does not extend to
  it;
- a participant who no longer controls the node may pause only their own session; they may not finish
  another worker's session and may not complete the leaf.

`CanView` (ADR 0032's separate read predicate) is untouched.

### 6. Archived leaves are never silently restored

ADR 0044 already makes an archived node's leaf closed to new sessions independent of its achievement.
This ADR adds no bypass: `ReopenAndStartWorkAsync` still requires the node to be un-archived first
(the leaf's `sessionStartClosed` predicate must be false apart from the achievement half, i.e. the
archive half must already be clear before the composite runs). The application layer names both
blockers when both are true and links to the existing Restore operation; a later, separate product
decision may add an elevated "Restore, reopen, and start" composite, but this ADR does not create one.

## Framework Design Guidelines review note (Stage 1 public surface)

Reviewed against `Framework_Design_Guidelines_Essentials.md` per the public-API-discipline
convention: `CompleteLeafAsync`/`ReopenAndStartWorkAsync` and their request/result types
(`CompleteLeafRequest`, `CompleteLeafResult`, `ReopenAndStartWorkRequest`, `ReopenAndStartWorkResult`,
`ExpectedActiveSession`) are purely additive members on `IWorkCommands`/`IWorkSessionCommandPort` and
new sealed immutable records — no existing public member's shape, name, or behavior changes. Naming
follows the established pattern (`{Verb}{Noun}Request`/`Result`, matching `StartWorkRequest`/
`SetAchievementRequest`); `ExpectedActiveSession` is a small, self-explanatory paired-value type
rather than a bare tuple, consistent with the guidelines' preference for a named type once a value
pair crosses an API boundary and is referenced by more than one member (here, the whole
`EquatableArray<ExpectedActiveSession>` on `CompleteLeafRequest`). The two new domain policies
(`WorkSessionAccessPolicy.CanFinishSession`, `LeafReopenAndStartAccessPolicy.CanReopenAndStartFor`)
are pure static predicates with the same `(roles, ...booleans...) -> bool` shape every existing
policy in `JobTrack.Domain.Authorization` already uses, so no new calling convention is introduced.
`PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` for `JobTrack.Domain` and `JobTrack.Application`
are updated to declare every new public member; M6 has passed, so ADR 0013's compatibility policy
treats this addition as a compatibility commitment going forward.

## Consequences

- `IWorkCommands` and the relevant persistence port gain `CompleteLeafAsync` and
  `ReopenAndStartWorkAsync`, each single-transaction, each independently subject to the Framework
  Design Guidelines review the plan's Stage 1 requires before the public surface freezes (M6 has
  passed; ADR 0013's compatibility policy applies).
- `WorkSessionAccessPolicy.CanManage` gains the narrow self-finish exception in §5; its unit tests and
  XML documentation must state the exception explicitly rather than leave it as an unstated special
  case inside `controls`.
- `AchievementTransitions`/`SetAchievementAsync` gain no new edges; `CompleteLeafAsync` reuses the
  existing `InProgress -> Success` edge, `ReopenAndStartWorkAsync` reuses the existing
  `terminal -> Waiting` edge plus ADR 0038's `Waiting -> InProgress` edge — no new transition graph
  entries are required in either ADR 0001 or ADR 0038.
- ADR 0044's closed-leaf predicate, stable failure identifiers, and per-leaf advisory
  lock/serialization are reused unchanged by both new composites; this ADR adds no new lock domain.
- `docs/database-entities.md`'s reopening-authority sentence, `docs/ownership-model.md`'s
  session-authorization section, and `docs/api/external-http-api-reference.md`'s achievement
  transition row are updated to describe the composite authority in §2 and the self-finish exception
  in §5, rather than repeating ADR 0001/0032's original, now-partially-superseded wording.
- `jobtrack_spec_codex.md` needs no additional normative text beyond what already exists in §4.4.1
  (closed leaves) and §5 (achievement) — this ADR does not change either rule, only who may invoke the
  reopen path and how many operations it takes; the spec correctly stayed silent on reopening
  authority (an ADR-only concern) and remains silent.
