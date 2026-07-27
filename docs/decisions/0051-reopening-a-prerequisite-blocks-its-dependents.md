# ADR 0051: reopening a prerequisite is always permitted; its dependents become blocked

**Status:** Accepted
**Supersedes:** the dependent-work rejection added to `ReopenAndStartWorkAsync` alongside ADR 0044's
leaf-closure serialization — "Reject stale reopen attempts once dependent work is active."

## Context

A job that is closed as `Success` is frequently closed wrongly: work was missed, the outcome was
recorded on the wrong node, or more work turned up afterwards. Reopening it is the correction, and
ADR 0045 §2 deliberately authorizes reopen-and-start more widely than any other achievement
transition for exactly that reason.

`ReopenAndStartWorkAsync` nonetheless refused whenever the leaf was `Success` and any job that
requires it — or any leaf beneath such a job — held an unfinished `WorkSession`, raising
`ConcurrencyConflictException`. Three things were wrong with that:

- **It was the wrong failure.** `ConcurrencyConflictException` means "your version is stale, re-read
  and retry", and `/Jobs/Work` reports it as "Someone else changed this leaf since the page was
  loaded." Nothing had changed and no reload could ever clear it.
- **It was written for a race but fired on steady state.** The check was introduced to serialize a
  reopen against a dependent's *concurrent* readiness decision, but it made no distinction between
  work that started during the request and work that had been sitting there — including a session
  someone forgot to pause a week ago. In that state the leaf could not be reopened at all.
- **It was not the rule anywhere else.** `SetAchievementAsync`'s own reopening path never carried it,
  so the same `Success -> Waiting` transition succeeded from `/Jobs/Work`'s "Change outcome"
  dropdown and failed from "Reopen and start session" on the same page.

The underlying question — what *should* happen to a dependent whose prerequisite is reopened
underneath it — had never been decided. The rejected check answered it by refusing the correction,
which protects the dependent's work by making the truth unrecordable.

## Decision

**Reopening a job is never refused on account of a job that depends on it.** Neither
`ReopenAndStartWorkAsync` nor `SetAchievementAsync` consults dependent work, dependent achievements,
or dependent sessions when leaving a terminal achievement. The rejection is removed from both
persistence providers.

**The dependent absorbs the consequence, as a block.** Once its prerequisite is no longer `Success`,
a dependent is not ready (`ReadinessCalculator`, unchanged), and an unready leaf cannot reach a
terminal achievement: `CompleteLeafAsync` and `SetAchievementAsync` raise
`PrerequisiteBlockedException`, which is exactly what they have always done for an unsatisfied
prerequisite. It regains the ability to close when the prerequisite succeeds again.

**A blocked dependent can still be reopened itself.** Readiness gates *closing* a job and *starting*
work on it; it never gates resuming one. A dependent that had already closed as `Success` when its
prerequisite was reopened is blocked, and reopening it too is usually the next step of the same
correction — so `Success -> Waiting` stays available to it. The one route that is not available is
`ReopenAndStartWorkAsync`, because that starts a session: it raises `PrerequisiteBlockedException`
like any other start. The staff UI therefore withdraws "Reopen and start session" (and Start /
Start for…) while a leaf is blocked and points at "Change outcome -> Waiting" instead.

**A live dependent session is not disturbed.** Reopening does not end sessions, change the
dependent's achievement, or reach into the dependent's row at all. A worker mid-session keeps
working and keeps recording time; only the *ending* is withheld. (Starting a *new* session on a
blocked leaf remains barred, as it always has been — that gate is unchanged.)

**The actor is warned before, and the dependent is told after.** `IJobQueries.GetLeafWorkPageAsync`'s
`HasActiveDependentWork` (already present, now purely advisory) drives the warning on the reopen
form; the dependent's own page shows the Blocked pill for any live achievement — not only the
Waiting-and-idle case it was previously limited to — and withdraws its completion affordances and
terminal "Change outcome" options while blocked, rather than offering a button the command must
refuse.

**Serialization is retained, and is the only thing the removed check was right about.** A reopen and
a concurrent dependent completion must not both commit from a snapshot the other invalidated.
PostgreSQL keeps taking the reopened node's own advisory lock (`LeafReadiness`'s
`additionallyLockedRequiredJobId`, which is why the parameter exists); SQLite keeps its
transaction-wide writer serialization. The two are now asymmetric rather than mutually exclusive:
the reopen always wins its own outcome, and the dependent's competing action succeeds only if it
committed while the prerequisite was still `Success`.

## Consequences

- The reported bug is fixed: a `Success` leaf with a live dependent reopens normally, and no
  concurrency message is shown for a state nothing concurrently changed.
- The two providers' "reopen vs. dependent start" race tests assert the new asymmetric contract
  instead of "exactly one succeeds".
- `PrerequisiteReadinessSerialization.HasActiveDependentWorkAsync` survives as a read-only query
  behind `IPrerequisiteQueryPort` for the warning. No command gates on it. Its interface doc no
  longer describes it as the counterpart of an in-transaction check.
- A blocked in-progress leaf is a normal, expected state with a name and a pill, alongside Paused
  (ADR 0045). It is not an error, and it is not a reason to end anyone's session.
- Nothing changes for a leaf whose *own* prerequisites are unsatisfied: those still gate
  reopen-and-start (it starts a session), and `ReopenAndStartWorkAsync` still raises
  `PrerequisiteBlockedException` for them. The asymmetry is deliberate — the prerequisites a leaf
  *has* gate its work; the prerequisites a leaf *satisfies* for others never do.

## Where this is enforced

| Behaviour | Test |
| --- | --- |
| Reopen permitted with a live dependent session | `WorkSessionCommandPortContractTestsBase.Reopening_a_successful_prerequisite_is_permitted_while_a_dependent_session_is_live` |
| Dependent cannot complete while blocked | `WorkSessionCommandPortContractTestsBase.A_dependent_with_a_live_session_cannot_be_completed_while_its_prerequisite_is_reopened` |
| Block clears when the prerequisite succeeds again | `WorkSessionCommandPortContractTestsBase.A_dependent_can_be_completed_once_its_reopened_prerequisite_succeeds_again` |
| Same, via `SetAchievementAsync`'s reopen path | `AchievementCommandPortContractTestsBase.Reopening_a_successful_prerequisite_is_permitted_and_blocks_its_in_progress_dependent_from_closing` |
| Reopen/dependent-start serialization, both providers | `{PostgreSql,Sqlite}WorkSessionCommandPortTests.Concurrent_reopen_of_a_former_prerequisite_vs_dependent_start_leaves_a_consistent_final_state` |
| A dependent already closed as Success can still be reopened | `AchievementCommandPortContractTestsBase.A_dependent_already_closed_as_success_can_still_be_reopened_after_its_prerequisite_is_reopened` |
| End to end through the web UI | `LeafWorkTests.Reopening_a_prerequisite_with_a_live_dependent_succeeds_and_blocks_the_dependent_from_completing` |
| Blocked dependent offers no terminal outcome | `LeafWorkTests.A_blocked_dependent_offers_no_terminal_outcome_option` |
| Blocked closed dependent: reopen-without-starting offered, reopen-and-start refused with a message | `LeafWorkTests.A_closed_dependent_can_still_be_reopened_after_its_prerequisite_is_reopened` |
