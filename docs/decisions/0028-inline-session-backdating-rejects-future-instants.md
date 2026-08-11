# 0028: Inline session start/finish accept a backdated instant, rejecting future ones

## Status

Accepted.

## Context

Recording work is the app's most common action. Before this decision, `StartSessionAsync` and
`FinishSessionAsync` captured "now" only; recording a session that already started, or backdating
when it finished, required navigating to the separate `CorrectSession` flow, which mandates a
`Reason` and produces an audit "before" record — appropriate for editing an already-recorded
session, but disproportionate for a first-time entry of a session that simply wasn't logged at the
moment it happened.

## Decision

`StartSessionRequest.StartedAt` and `FinishSessionRequest.FinishedAt` are now optional
(`Instant?`). `null` keeps today's behaviour — the command captures one clock value ("now") itself.
A caller may instead supply a past instant to record a session that already started or finished;
this is a first-time entry of that instant, not a correction, so — unlike `CorrectSessionAsync` —
it carries no `Reason` and produces no audit "before" value.

A supplied instant must not be in the future: `StartSessionAsync` throws
`InvariantViolationException` with `ConstraintId` `"work-session-start-in-future"` if
`StartedAt > now`; `FinishSessionAsync` throws with `ConstraintId`
`"work-session-finish-in-future"` if `FinishedAt > now` (in addition to the pre-existing
`"work-session-invalid-interval"` check that a finish instant must be after the session's start).
This is a new rule, not spec-mandated — the spec does not restrict `CorrectSessionAsync`'s existing
instants to the past, and that command is deliberately left as-is: it edits history under an
explicit reason, so an operator correcting a mistaken future entry is a legitimate, already-audited
use case. The new inline path has no reason field to explain an unusual entry, so future-dating is
rejected outright there instead.

A backdated `StartSessionAsync` can now also collide with a past, already-finished session — this
was previously impossible, since "now" can never fall inside history. That collision surfaces as
`InvariantViolationException` with `ConstraintId` `"work-session-overlap"`, reusing
`CorrectSessionAsync`'s existing constraint id for the same underlying schema version 0007 overlap
enforcement (GiST exclusion constraint on PostgreSQL, immediate triggers on SQLite). The pre-existing
"already active" case (`"work-session-already-active"`) is now checked application-side before the
insert, rather than solely relying on distinguishing which schema constraint a database exception
names — PostgreSQL does not guarantee which of two simultaneously-violated constraints (the partial
unique index vs. the exclusion constraint) it reports for a given statement, so an application-side
pre-check is the only way to keep the two outcomes reliably distinct for the common, non-concurrent
case; the database constraints remain the backstop for genuine concurrent races.

## Consequences

- `JobTrack.Web`'s `Browse` page exposes inline Start/Finish controls per leaf row, with an optional
  backdated-time field, without navigating to the `Work` page.
- `IJobQueries` gains `GetActiveSessionsAsync`, a batch query (mirroring `GetJobSummariesAsync`'s
  by-ids shape) so the Browse list can show an "active session" indicator per row without a
  per-row lookup.
- Backdating remains reason-free and unaudited only for a session's *first* recorded instant;
  editing an already-recorded instant still requires `CorrectSessionAsync` and its `Reason`.
