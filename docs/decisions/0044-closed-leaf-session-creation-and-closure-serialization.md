# 0044: Closed-leaf session creation and closure-vs-active-session serialization

## Status

Accepted.

## Context

`StartWorkAsync` (ADR 0038) only auto-advances a `Waiting` leaf; nothing in `StartSessionAsync` or
`StartWorkAsync` checks whether the leaf's `Achievement` is already terminal (`Success`,
`Cancelled`, `Unsuccessful`) or the node is archived. A leaf can therefore receive a brand-new
`WorkSession` — including a backdated one — after it has been marked done or archived, silently
reopening work that the achievement/archive state says is finished. The converse gap also exists:
transitioning a leaf to a terminal achievement, or archiving its node, does not check whether a
`WorkSession` is currently active on it, so a clock can be orphaned mid-flight with no session ever
reaching a `FinishedAt`.

Both gaps are the same underlying omission — closure state (terminal achievement, archived node)
and "does an active session exist" have never been serialized against each other — so this ADR
closes them together rather than as two unrelated fixes. It amends ADR 0001 (achievement states)
and ADR 0038 (auto-advance) without changing either's existing transition graph or auto-advance
rule, and amends ADR 0012 (PostgreSQL lock keys) by adding one new lock domain.

## Decision

### Closed predicate

```text
sessionStartClosed(leaf) =
       leaf.achievement in { Success, Cancelled, Unsuccessful }
    OR leaf.jobNode.archivedAt is not null
```

### Rules

1. `StartSessionAsync` and `StartWorkAsync` reject a new session — including a backdated one whose
   `StartedAt` predates the closure — whenever `sessionStartClosed(leaf)` is true at the instant of
   creation. Current state controls; a session that would have been valid when the backdated instant
   occurred does not bypass a closure that has since been recorded. Both commands only ever create an
   *active* (`FinishedAt` null) row, so this rule is, at the command layer, exclusively about active
   sessions.

   The database predicate distinguishes the two halves of `sessionStartClosed` for a **new row that
   is already finished at insert** (relevant only to direct/bypass writes and to subtree import,
   never to `StartSessionAsync`/`StartWorkAsync`): an archived leaf's node rejects any new
   `WorkSession` row outright, active or already finished — archival is deliberately the harder
   closure, admitting no further operational backfill at all. A leaf whose achievement is merely
   terminal (not archived) rejects only a new *active* row; inserting an already-finished row remains
   permitted, because subtree import (see below) legitimately inserts finished historical sessions and
   sets the leaf's terminal achievement inside the same transaction, and a deferred constraint trigger
   evaluates only the final committed row state, not statement order — so this asymmetry has to be
   encoded in the predicate itself, not assumed from write ordering.
2. `SetAchievementAsync` rejects a transition into `Success`, `Cancelled`, or `Unsuccessful` while any
   `WorkSession` on that leaf is active, regardless of which worker holds it. Every other transition
   (including reopening a terminal state back to `Waiting`, per ADR 0001's existing authority) is
   unaffected.
3. `ArchiveAsync` rejects archiving a leaf node while any `WorkSession` on that leaf is active.
   Archiving a branch does not recurse into descendant state; a descendant leaf is closed under the
   predicate above only when that leaf's own node is archived directly. Archiving a node with no
   attached `LeafWork` is unaffected by this rule.
4. `FinishSessionAsync` remains permitted regardless of concurrent closure attempts. Under a race
   between a finish and a terminal-transition/archive attempt on the same leaf, exactly one
   logically consistent outcome is committed: either the finish commits first and the closure then
   proceeds against zero active sessions, or the closure loses because an active session is still
   visible to it. Neither ordering may strand a session with no `FinishedAt` reachable.
5. `CorrectSessionAsync` remains permitted against a closed leaf for a session that is already
   finished (editing `StartedAt`/`FinishedAt`/worker of a historical row), but is rejected if the
   correction would leave the session active (`FinishedAt` cleared or absent) while the leaf is
   closed under the predicate above.
6. Reopening a terminal achievement to `Waiting` removes the achievement half of the predicate,
   subject to ADR 0001's existing reopening authority; starting the next session then auto-advances
   `Waiting -> InProgress` under ADR 0038, unchanged. An archived node must additionally be restored
   before a new session can start — reopening achievement alone leaves the archive half of the
   predicate still true.
7. None of the above deletes, truncates, or hides existing session history. A closed leaf's finished
   sessions remain visible and costable indefinitely.

### Stable failure identifiers

Following the existing `InvariantViolationException`/`ConstraintId` convention (ADR 0028), one
identifier means one condition, used identically across both database providers, the persistence
layer, HTTP problem mapping, and browser display:

- `work-session-leaf-closed` — a start, or a correction that would leave a session active, was
  attempted against a leaf currently closed under the predicate above.
- `leaf-closure-active-sessions` — a terminal achievement transition or a leaf archive was attempted
  while at least one session on that leaf is still active.

### Database enforcement and serialization

Rule 4's race guarantee requires that "is any session active" and "commit the closure" happen inside
one serialized unit per leaf, not as an application-side check followed by a separate write — a
plain pre-check-then-write is vulnerable to the same write-skew shape ADR 0012's prerequisite-cycle
case already demonstrated for a different invariant. Per ADR 0012's existing convention, this adds
one new advisory lock domain rather than a bespoke mechanism:

- **leaf session closure** — keyed on the leaf's `job_node_id` (the same id space `LeafWork` and
  `WorkSession` both hang off), acquired via `pg_advisory_xact_lock` by session start, session
  finish, the terminal-achievement transition, and leaf archive, so all four contend on the same
  per-leaf key and one commits at a time. SQLite's existing `BEGIN IMMEDIATE`/single-writer model
  already serializes these paths without a comparable lock primitive; its named immediate triggers
  enforce the same contract and are proved by the same race tests as the PostgreSQL path, per §6.6's
  "prove atomicity under contention" requirement.
- Named triggers/functions on PostgreSQL and named immediate triggers on SQLite enforce the predicate
  directly against `work_session`, `leaf_work.achievement_id`, and `job_node.archived_at` — never an
  unlocked trigger read that only sees committed state, and never an application-only pre-check as
  the sole guard. The application-side check (Stage 3 of the implementing plan) exists for a clear,
  early error message; the database constraint is the authority under races and direct-write bypass.

### Import interaction

Subtree import (which may bring in already-completed historical work in one transaction) is not
exempt from this predicate — it satisfies it by ordering its own writes: attach an open `LeafWork`,
insert already-finished session rows, and only then set the terminal imported outcome, so no active
session is ever left on the leaf by commit time (satisfying rules 2/3's "no active session" check
regardless of statement order, since a deferred trigger sees only final state). The insert itself is
independently permitted by rule 1's finished/terminal exemption above — a finished row is rejected
only when the leaf is archived, never merely because its achievement is (or becomes, in the same
transaction) terminal.

## Consequences

- `WorkSessionFailureDisplay` and HTTP problem-details mapping gain the two new stable identifiers,
  mapped through the existing conflict/invariant status conventions — no new web-only failure
  category.
- `StartSessionAsync`/`StartWorkAsync`/`SetAchievementAsync`/`ArchiveAsync`'s XML documentation and
  `PublicAPI.Unshipped.txt` gain the new documented exception conditions.
- A terminal or archived leaf's UI must explain *why* starting is unavailable (achievement, archive,
  or both) while leaving its Sessions history fully visible and correctable — this ADR constrains
  the underlying commands; the browser presentation is specified separately in the implementing
  plan.
- No change to `Achievement`'s enum values, permitted-transition graph, or `StartWorkAsync`'s
  auto-advance rule; both ADR 0001 and ADR 0038 remain otherwise in force.
