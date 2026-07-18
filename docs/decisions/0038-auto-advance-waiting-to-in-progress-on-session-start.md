# 0038: Starting work auto-advances Waiting to InProgress

## Status

Accepted.

## Context

Recording progress on a fresh leaf required three separate manual steps, each a distinct page or
click: attach `LeafWork` (`AttachLeafWorkAsync`), start a session (`StartSessionAsync`), and
separately transition `Achievement` from `Waiting` to `InProgress` (`SetAchievementAsync`, ADR
0001). Nothing in the UI exposed this at all from the `AwaitingProgress` dashboard — precisely the
page whose job is to surface leaves needing exactly this action.

ADR 0001 established `Achievement` as a deliberate, manually-driven state machine, kept separate
from `WorkSession` recording: a job manager can mark something `InProgress` without anyone clocked
in, and someone can clock time without formally advancing the achievement. That separation remains
correct for every other transition. But for the specific case of starting the *first* session on a
leaf that is still `Waiting`, requiring a third, separate manual step before the leaf's state
reflects reality is unnecessary friction with no offsetting benefit — nobody starts clocking time on
a job and intends to leave it recorded as "not started."

## Decision

`IWorkCommands.StartWorkAsync` (`IWorkSessionCommandPort.StartWorkAsync` at the persistence layer)
is a new one-click composite, run inside one transaction:

1. Attach `LeafWork` if the node doesn't already have it (identical validation to
   `AttachLeafWorkAsync`: the node must be a childless non-root node).
2. If the leaf's achievement is `Waiting` (whether just-attached or already attached), advance it to
   `InProgress` — reusing `AchievementTransitions`' existing `Waiting -> InProgress` edge, but
   applied directly rather than through `SetAchievementAsync`, since this step carries no caller
   `Reason` and needs no optimistic-concurrency `Version` (it operates on the entity already loaded
   in the same transaction). The audit event's reason is the fixed string `"Advanced automatically on
   session start"`, distinguishing it from a manually-reasoned transition.
3. Start the session (`StartSessionAsync`'s existing logic: readiness recheck, active-session and
   overlap checks).

If the leaf's achievement is already `InProgress` (e.g. a different worker already has an active
session), step 2 is a no-op — `AchievementTransitions.IsPermitted` only allows `Waiting ->
InProgress`, not `InProgress -> InProgress`, and no second audit event is written.

Every other transition is unchanged from ADR 0001 and remains fully manual: `InProgress -> Success`
and both `Cancelled`/`Unsuccessful` completions still require an explicit `SetAchievementAsync` call
with a `Reason`, and reopening a terminal state back to `Waiting` still requires the elevated
Administrator/JobManager authorization ADR 0001 established. `AttachLeafWorkAsync` and
`SetAchievementAsync` themselves are unchanged and remain independently callable — `StartWorkAsync`
is an additive convenience, not a replacement.

Because this runs inside one transaction, a failure at any step (blocked prerequisite,
already-active session for this worker) leaves no partial state: a blocked leaf's `StartWorkAsync`
call rolls back its own attach too, rather than leaving an orphaned `LeafWork` row with no session.

## Consequences

- `JobTrack.Web`'s `Browse` (both the per-row quick action and a leaf's own detail view),
  `AwaitingProgress`, and `Work` pages each expose a single "Start work" action in place of the
  previous separate "Attach"/"Start"/"set to InProgress" affordances.
- A leaf can still be marked `InProgress` (or any other state) without an active session, and a
  session can still be started without this composite (`StartSessionAsync` remains available and
  unchanged) — `StartWorkAsync` only removes friction for the common one-click case.
- `AwaitingProgress` entries (already showing leaves with no `LeafWork` attached, per the
  `AwaitingProgressCalculator` fix immediately preceding this ADR) now have a direct one-click path
  to becoming actively worked, rather than only being visible.
