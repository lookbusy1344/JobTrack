# ADR 0047: `CompleteLeafAsync` records any terminal achievement, not Success only

**Status:** Accepted
**Supersedes:** ADR 0045 §1/§3's restriction — "Records `Achievement.Success` only... `Cancelled`
and `Unsuccessful` have no atomic composite and remain manual `SetAchievementAsync` calls."

## Context

`/Jobs/Work`'s "Completion options" disclosure lets a leaf owner end its active sessions and record
an outcome in one action, but it could only ever record `Achievement.Success` — recording `Cancelled`
or `Unsuccessful` while sessions were still active meant pausing each session individually first
(no atomic path), then a separate `SetAchievementAsync` call through "Change outcome". For a leaf
with several concurrent workers this is exactly the two-step, non-atomic sequence ADR 0045 built
`CompleteLeafAsync` to avoid for the Success case.

`AchievementTransitions.IsPermitted(InProgress, to)` already permits `Success`, `Cancelled`, and
`Unsuccessful` identically — the Success-only restriction was a deliberate scope decision for
ADR 0045's first cut, not a domain invariant. Nothing in `AchievementTransitions` or the persistence
triggers (`leaf-closure-active-sessions`, `achievement-transition-not-permitted`) special-cases
which terminal value is being reached.

## Decision

`CompleteLeafRequest` gains `FinalAchievement` (`Achievement`, default `Success` — every existing
caller that doesn't set it keeps recording Success exactly as before). Both persistence providers'
`CompleteLeafAsync` use `request.FinalAchievement` instead of the literal `Achievement.Success` in
the permitted-transition check and the assignment; `FakeWorkSessionCommandPort` does the same. The
command still only reaches this from `Achievement.InProgress` (unchanged), so in practice the field
accepts exactly `Success`, `Cancelled`, or `Unsuccessful` — anything else throws
`InvariantViolationException` (`"achievement-transition-not-permitted"`), the same failure
`SetAchievementAsync` already produces for an impermissible transition.

The external HTTP API's `CompleteLeafBody` gains a matching optional `FinalAchievement` field
(`null` wire default = `Success`) — additive and optional, so per ADR 0030 this is not a breaking
change to the shipped route; no client proof update is required.

`/Jobs/Work`'s "Completion options" disclosure gains a status dropdown (Success/Cancelled/
Unsuccessful) alongside the existing backdate and completion-note fields, submitting
`finalAchievement` to the `Complete` handler. The fixed structured audit reason (`"Completed from
the leaf work page"`) is unchanged and applies regardless of which achievement is recorded — it
identifies the composite that was used, not the achievement it produced.

## Consequences

- Ending a leaf's active sessions and recording Cancelled/Unsuccessful is now one atomic transaction,
  matching Success's existing guarantee.
- `IWorkCommands.CompleteLeafAsync`'s doc comment, both persistence providers, the fake port, and the
  contract/application-layer tests were updated together — see
  `WorkSessionCommandPortContractTestsBase.Completing_a_leaf_with_a_non_success_final_achievement_records_it`.
- A caller that only wants to change the achievement of an already-paused leaf (no active sessions to
  confirm) can still use either `CompleteLeafAsync` with an empty `ExpectedActiveSessions` or
  `SetAchievementAsync` directly — both remain valid, matching the existing zero-session Success path.
