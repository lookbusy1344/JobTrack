# Unified leaf work, completion, and reopening workflow

**Date:** 2026-07-22
**Status:** Implemented — all stages landed 2026-07-22; ADR 0045 is Accepted. Stage 1's composites
and Stage 2's shared `WorkSessionCommandPortContractTestsBase` coverage (happy path,
authorization, concurrency, audit trail, rollback, and reopen authority) run identically against
both providers. Provider-specific tests cover every §6 race and assert the exact committed
achievement/archive/session state: complete vs start/finish/correction, reopen-and-start vs
archive/another reopen, and reopen of a formerly successful prerequisite vs dependent work
starting. The last case reuses ADR 0044's ordered per-leaf closure locks on PostgreSQL and SQLite's
single-writer transaction, preventing both operations from committing from the same readiness
snapshot. A genuine provider divergence remains deliberate: SQLite's immediate leaf-closure
trigger requires `CompleteLeafAsync` to save finished sessions before the achievement update;
PostgreSQL's deferred trigger permits one batch, while both still commit once atomically.

Stage 3 adds `POST /jobs/{nodeId}/complete` and `POST /jobs/{nodeId}/reopen-and-start-session`,
documented in the OpenAPI contract test's exact route set and the external API reference, exercised
by direct-HTTP web-integration tests (cookie and bearer, authorization, conflict mapping) and
extended into `samples/JobTrack.ExternalApiClient` (no `JobTrack.*` project reference) with the
`ExternalApiClientProofTests` suite exercising `CompleteLeafAsync` against both real providers
through the live web host.

Stage 4 adds `IJobQueries.GetLeafWorkPageAsync`/`LeafWorkPageResult`: one bounded projection —
node context, achievement/version, readiness, every active session (never collapsed), direct-
dependent count, and actor-specific `Can*` rendering hints for both new composites — composed
entirely from already-batched, already-fixed-cost existing ports (`IJobBrowseQueryPort`,
`ILeafWorkQueryPort`, `IReadinessQueryPort`, `IWorkSessionQueryPort`, `IPrerequisiteQueryPort`), so
no new provider SQL or contract-test tier was needed; both real providers already satisfy it
transitively. Application-layer tests cover concurrent-session non-collapse, controller/prior-
participant/non-participant reopen authority, dependent counting, and the no-`LeafWork` case.
Discovered and fixed a `FakeJobNodeCommandPort` test-fixture gap along the way: `HasLeafWork` is a
derived structural fact from the fake's internal attach-tracking, not the raw seeded field.

Stage 5 reshapes `/Jobs/Work` into the masthead/work-rail described in §5.1: achievement + readiness
+ active-session pills, the "How is this job ending?" Pause work/Complete job decision (multi-worker
review rendered inline, no JS required), the Reopen-and-start form (reason with quick-choice
`<datalist>` plus free text, target-worker picker gated by `CanReopenAndStartForOthers`, optional
backdate), and a native `<details>` "Change outcome" disclosure for Cancel/Mark unsuccessful/Reopen
without starting. Completion supports one shared optional backdate and note; a multi-session
completion explicitly names the exact reviewed workers before submission. `/Jobs/Achievement` is
now a compatibility redirect to `/Jobs/Work#status`; Browse and Awaiting Progress route End-session
actions to `/Jobs/Work`, and Browse no longer embeds a competing interactive Sessions panel. This
stage also closed a real gap
left open at the end of Stage 2: ADR 0045 §5's self-finish exception
(`WorkSessionAccessPolicy.CanFinishSession`) was defined in Stage 1 but never wired into
`FinishSessionAsync`'s authorization on either provider until now, with new contract tests proving a
worker can still pause their own session after losing node control. The full golden path (start →
pause → complete → reopen-and-start, plus the advanced disclosure) was exercised in a real Chrome
session against a live SQLite-backed dev server, including a mobile-width (390px) layout check.

**Recorded variance:** no `Restore` command or page exists in the product, so the archived-leaf
notice states the prerequisite in prose rather than linking to a nonexistent route. Stage 6 did not
retain a transient screenshot gallery: the dual-provider browser suite is the repeatable visual-QA
evidence, exercising phone/tablet/desktop viewports, 320-CSS-pixel reflow (stricter than the planned
200% check), keyboard/focus behavior, reduced motion, and axe. This is an accepted evidence-format
variance, not outstanding implementation work.

**Correction after landing:** the plan's own §5.6 axe/keyboard/reflow browser-test matrix already
existed in this codebase before this stage (`LeafWorkSessionBrowserTests`/
`PrerequisitesAchievementBrowserTests`, dual-provider, `Deque.AxeCore.Playwright`) and was not
something this stage needed to add — the final gate passed all 195 `JobTrack.Web.EndToEndTests` and
all 2,770 solution tests. Running that suite is what caught two real regressions the manual Chrome
walkthrough missed: a leaf with no
`LeafWork` attached yet had no visible action at all (StartWorkAsync's whole point is a one-click
attach-and-start for exactly that state), and `CanManageSessions`/`CanComplete` were computed only
once `LeafWork` existed, hiding "Start for…" from a controlling owner on a brand-new leaf. Both are
fixed. This is the concrete case for running the existing suite, not just a manual browser pass,
before calling a Razor rework done.
**Depends on:** ADR 0001 (achievement states and reopening authority), ADR 0032 (owner-gated
session management), ADR 0038 (session start auto-advances `Waiting` to `InProgress`), ADR 0041
(session visibility), ADR 0044 (closed-leaf session invariant and serialization), ADR 0045 (unified
leaf completion and reopen-and-start authority, closing this plan's Stage 0), the accepted M6
and M8 gates, the mandatory implementation order in `jobtrack_impl_plan.md`, and the Console design
language.

## 1. Outcome

Make `/Jobs/Work?leafNodeId={id}` the single operational surface for a leaf's current status and its
Sessions. The common workflow becomes:

```text
Start session -> work -> End session -> How is this job ending?
                                      |-- Pause work
                                      |    `-- finishes only this session
                                      `-- Complete job
                                           |-- finishes the active session(s)
                                           `-- records Success

Completed -> Reopen and start session
             |-- records the audited reopen
             |-- advances back to InProgress
             `-- creates the new active session
```

Each mutating outcome is one explicit, atomic command. A quick action never guesses whether ending
a session means pausing or completing: it opens the unified page at that decision. The UI presents
one obvious next step for the current state and moves exceptional outcomes and historical correction
into progressive disclosure.

This plan does not make an ordinary `FinishSessionAsync` imply success and does not make an ordinary
`StartWorkAsync` silently reopen a terminal leaf. Those operations are ambiguous in isolation:
finishing a session can mean pause, and reopening a successful leaf can regress readiness for
dependent jobs. The new composites remove clicks without erasing those distinctions.

## 2. Why the current product feels trapped

The database and domain already have a recovery path, but the browser does not present it as a
workflow:

1. ADR 0044 correctly rejects a new active session while the leaf is terminal or archived.
2. ADR 0001 permits `Success | Cancelled | Unsuccessful -> Waiting`, but only a Job Manager or
   Administrator may do it and a reason is mandatory.
3. ADR 0038 then makes the next session start advance `Waiting -> InProgress` automatically.
4. The user must discover `/Jobs/Achievement`, choose `Waiting`, enter a reason, return to Browse or
   Sessions, and start again. An owning Worker cannot perform step 2 at all.
5. `/Jobs/Achievement` renders the whole enum rather than only legal, authorized actions, so it can
   offer a choice which the eventual command answers with Access Denied.
6. Finishing the last session only finishes its interval. The user must then discover Achievement
   and make a second state transition to `Success`.

The present behavior is therefore internally consistent but operationally poor. For a Job Manager
or Administrator, the existing emergency recovery is:

```text
Achievement -> Waiting (with reason) -> Sessions -> Start session
```

For a Worker, reopening is intentionally impossible under ADR 0001 and requires escalation. The UI
does not explain that authority boundary or provide a clear next step.

## 3. Confirmed product decisions

The product owner confirmed these choices on 2026-07-22. They are requirements for the Stage 0 ADR,
not open implementation options.

### 3.1 Who may reopen?

Reopening any terminal outcome is permitted when the actor is an enabled operational employee and
at least one of these is true:

- the actor recorded any previous session on this leaf;
- the actor controls the leaf through direct or ancestor node ownership; or
- the actor is a Job Manager or Administrator.

This deliberately amends ADR 0001's Job Manager/Administrator-only restriction. A prior session
participant may use the composite to reopen and start a new session **for themselves**, even if they
no longer control the node. A controlling owner, Job Manager, or Administrator may select another
eligible workflow employee as the new session's worker. Historical participation does not grant the
right to start for someone else.

Disabled accounts, Requesters, and employees with no operational workflow role cannot acquire this
authority merely from historical session data. Reopening `Success` may make dependent work blocked
again, so the page presents the dependent impact before mutation and the command re-evaluates it in
the transaction.

### 3.2 What happens when several sessions are active at completion?

An authorized `Complete job` action atomically finishes the exact active-session
set the user reviewed and changes the leaf to `Success`. With one active session it submits
immediately. With two or more it opens a compact review naming every affected worker, then one
confirmation finishes all of them at the same captured instant.

### 3.3 Is completing the last session implicit?

No. A quick **End session** action opens the unified Work page at a focused decision
panel which asks **How is this job ending?** and presents two explicit outcomes:

- **Pause work** — finish only the selected session and leave the leaf `InProgress`;
- **Complete job** — finish the applicable active session(s) and record `Success`.

Making the existing finish action always imply success removes the ability to pause, produces false
success when more work remains, and becomes especially unsafe with concurrent workers. Routing
through the merged page adds one intentional choice while keeping the mutation itself atomic.

### 3.4 How should an archived completed leaf behave?

Do not silently restore it. Archival is the harder operational closure under ADR
0044. The unified page shows both blockers and links to Restore. After restoration, the user can use
`Reopen and start session`. A later product decision may add an elevated `Restore, reopen and start`
composite, but it is outside this workflow simplification.

### 3.5 What reason is recorded for normal completion?

Use a structured system reason such as `Completed from the leaf work page` so the
user does not encounter a second form after choosing completion, and offer an optional completion
note in progressive disclosure.

Reopening continues to require a user-supplied reason because it corrects a prior terminal decision;
provide quick choices (`Closed by mistake`, `More work was found`, `Work resumed`) plus free text.
The persisted audit value must record the resolved text, not merely a UI option code.

### 3.6 Who may end whose session?

The merged page works for the viewer's own session and for another worker's selected session, but
the permitted outcomes differ by authority:

- the worker named on an active session may always open **End session** and choose **Pause work** for
  that session, even if node ownership changed after the session began;
- a direct/ancestor controlling owner, Job Manager, or Administrator may choose **Pause work** for
  any selected active session on the leaf;
- **Complete job** requires normal achievement-management authority (controlling owner, Job Manager,
  or Administrator), because it ends every active session and changes the leaf to `Success`; and
- a participant who no longer controls the node may pause their own session but may not complete the
  leaf or end other workers' sessions.

This amends ADR 0032 narrowly for finishing one's own already-active session. Starting and
correcting sessions remain owner-gated unless another confirmed rule in this plan explicitly says
otherwise. It prevents an ownership change from making a worker's active clock impossible for that
worker to stop.

## 4. Domain semantics

### 4.1 Primitive commands remain precise

- `StartWorkAsync` starts open work and retains ADR 0038's `Waiting -> InProgress` auto-advance.
- `FinishSessionAsync` ends one interval and never changes achievement.
- `SetAchievementAsync` retains the canonical transition graph and continues rejecting a terminal
  transition while any session is active.
- Finished historical sessions remain independently correctable after completion.

These primitives stay available for integrations and complex workflows. The composites are
additive application operations, not UI orchestration across several `IJobTrackClient` calls.

### 4.2 Complete job composite

Add a single ACID operation, provisionally named `CompleteLeafAsync`, with an FDG review before the
public surface is frozen. It:

1. acquires ADR 0044's existing per-leaf closure lock;
2. reloads the leaf, achievement, readiness, authorization, and every active session;
3. verifies the caller's optimistic-concurrency token and that the active-session ids/versions
   exactly match the set shown in the confirmation;
4. captures one finish `Instant` for the whole operation, or parses one explicit backdated wall time
   through the viewer's zone at the boundary;
5. verifies that instant is later than every affected session start and is not in the future;
6. authorizes finishing every affected session and changing achievement;
7. finishes the active sessions;
8. changes `InProgress -> Success` with the audit reason; and
9. saves and commits once.

If any validation fails, neither sessions nor achievement change. The exact expected active set is
important: a newly started concurrent session must produce a refresh/conflict response, not be
stopped without having appeared in the user's review.

The common case contains exactly one active session. The same command also supports no active
sessions so an `InProgress` leaf which was previously paused can be completed without fabricating a
session. `Waiting -> Success` remains prohibited by the canonical graph; a never-started job can be
cancelled or marked unsuccessful through the advanced status actions, not falsely recorded as
success.

### 4.3 Reopen and start composite

Add a single ACID operation, provisionally named `ReopenAndStartWorkAsync`. It:

1. requires the leaf to be terminal and not archived;
2. acquires the same per-leaf closure lock;
3. reloads achievement/version, readiness, actor roles, node control, prior participant status,
   target-worker eligibility, and active sessions;
4. requires the confirmed reopening authority from section 3.1 and a non-blank user reason;
5. when authority comes only from prior participation, requires the target worker to be the actor;
6. verifies that the terminal version still matches the page;
7. records `terminal -> Waiting` with the supplied reason;
8. applies ADR 0038's `Waiting -> InProgress` transition with its existing automatic-start audit
   reason;
9. starts the requested worker's session at the captured or supplied start instant; and
10. saves and commits once.

If readiness, authorization, eligibility, overlap, time validation, or concurrency fails, the
terminal state remains unchanged. This property is the main reason not to implement the behavior as
two Razor Page calls.

Reopening a prior `Success` immediately re-evaluates normal readiness behavior for dependents under
ADR 0001. Before confirmation, the UI warns when the success currently satisfies one or more direct
dependents; it does not pretend the correction is locally isolated.

### 4.4 Multiple active sessions and other terminal outcomes

`Complete job` means `Success` only. `Cancelled` and `Unsuccessful` remain explicit outcomes in an
advanced **Change outcome** disclosure and retain ADR 0044's rule that active sessions must first be
paused. Atomic “finish all and close” behavior for negative outcomes is not part of this plan; do
not smuggle it into `CompleteLeafAsync`.

## 5. Unified page interaction design

### 5.1 Page responsibility and visual direction

Subject: one operational leaf. Audience: the worker doing it and the person coordinating it. The
page's single job: show what is happening now and provide the correct next action.

Keep the `/Jobs/Work` route. Replace the generic `Sessions` masthead with the leaf's actual title,
preceded by a small **Work** eyebrow and followed immediately by achievement/readiness. `Sessions`
remains the noun for the historical collection and the deep-link label from dense job rows.
`/Jobs/Achievement?jobNodeId={id}` becomes a compatibility redirect to
`/Jobs/Work?leafNodeId={id}#status`; there must not be two competing state-management pages.

Use the established Console palette, type, Bootstrap grid/utilities, and existing icons. Do not add
a new palette or bespoke responsive grid. The signature element is a compact **work rail**: current
achievement at its head, active worker intervals in its middle, and the one primary next action at
its end. It encodes the lifecycle rather than decorating the page.

```text
WORK
Replace failed drive                              [In progress]
Ready to proceed

+-- ACTIVE NOW ------------------------------------------------------+
| You · started 09:14                                                |
|                                                                    |
| How is this job ending?                                             |
| [Pause work]  [Complete job]                       [More actions ▾] |
+--------------------------------------------------------------------+

Sessions
[Everyone ▾]                                      [Start for…]
09:14—active  You
08:03—09:02    Morgan
...
```

At phone width the state and primary action come first; metadata and exceptional actions follow.
No action disappears merely because the viewport narrows.

### 5.2 One clear next step per state

| Leaf state | Active sessions | Primary action | Secondary/advanced actions |
|---|---:|---|---|
| No `LeafWork` / `Waiting` | 0 | **Start session** | Start for…, backdate, cancel |
| `InProgress` | viewer active; 1 total | **End session** -> pause/complete decision | Backdate finish, correction |
| `InProgress` | viewer active; several total | **End session** -> pause mine/complete all decision | Completion review names all workers |
| `InProgress` | others active | **Manage ending…** when authorized | Start mine, choose an exact worker, Sessions |
| `InProgress` | 0 | **Start session** | Mark complete, change outcome |
| terminal, reopen-authorized | 0 | **Reopen and start session** | Reopen without starting, correction/history |
| terminal, not reopen-authorized | 0 | none | Explain that a prior participant, node controller, Job Manager, or Administrator must reopen it |
| archived | any valid state | none | Explain Restore requirement; Sessions remains visible |

For a completed leaf, the empty action area must never merely say that starting is unavailable. It
states the recovery path. For example:

> Completed. To record more work, reopen this job and start a new session.

The authorized actor sees **Reopen and start session** directly beneath that sentence. Everyone
else sees who can perform it. A prior participant acting without node control can only start for
themselves. When archived as well, state both requirements.

### 5.3 End-session and completion interaction

- Browse, Awaiting Progress, and other compact surfaces never finish inline. Their **End session**
  link opens `/Jobs/Work?leafNodeId={id}&endSessionId={sessionId}#end-session`. The session id selects
  the intended interval for presentation only; the command reloads and authorizes it.
- The merged page places **How is this job ending?** directly after the active-session summary. Its
  two outcome controls are large, plainly worded native submit actions, not a select list or an
  ambiguous generic Save button.
- Exactly one active session: **Pause work** finishes only that session. **Complete job** finishes it
  and records `Success`. Either choice submits immediately. Completion feedback is `Job completed
  and session finished.` Refresh is a harmless GET through PRG.
- Several active sessions: **Pause work** identifies and finishes only the selected session.
  **Complete job…** expands an in-page review listing worker and active-since time for every session,
  followed by `Finish 3 sessions and complete job`. No JavaScript is required for the form to submit
  correctly.
- No active sessions: **Mark complete** remains available under the primary action area when the
  state is `InProgress`.
- **Pause work** says exactly what remains true afterward: `Ends this session; the job stays In
  Progress.` Do not use the current ambiguous `Finish / pause` label.
- Backdating is progressive disclosure and uses `BackdateInstant`; one completion instant applies
  to the complete active set. If that is wrong for a multi-worker case, finish/correct sessions
  individually before completion.

### 5.4 Reopening interaction

`Reopen and start session` expands a compact form containing:

- reason quick choices plus free text;
- target worker, defaulting to the viewer and reusing the existing Start-for authorization;
- optional backdated start; and
- a dependent-impact warning when applicable.

The submit label repeats the outcome: **Reopen and start session**. On success the page returns by
PRG with `Job reopened. Session started.` and the work rail shows `In progress` plus the new active
session. A separate advanced **Reopen without starting** action supports administrative correction
without inventing evidence that work occurred.

### 5.5 Advanced achievement actions

Remove the raw enum dropdown. Render only transitions which are both legal from the current state
and potentially authorized for the actor:

- **Mark complete** (`InProgress -> Success`);
- **Cancel job** (`Waiting | InProgress -> Cancelled`);
- **Mark unsuccessful** (`Waiting | InProgress -> Unsuccessful`);
- **Reopen without starting** (`terminal -> Waiting`, elevated only).

Every action names its consequence and requests a reason where the command contract requires one.
Server-side policy remains authoritative. Capability projection controls presentation only and must
be loaded in a bounded query, not recomputed in Razor.

### 5.6 Accessibility and resilience

- Native forms and buttons remain complete without JavaScript; disclosure scripting only improves
  focus and density.
- After expanding a completion/reopen form, focus moves to its heading or first required field; on
  validation failure it moves to the error summary.
- Active-session counts, status, warnings, and outcome are never conveyed by color alone.
- Confirmation text names the number of sessions and affected workers.
- Phone, tablet, desktop, keyboard-only, reduced-motion, and 200% zoom layouts are explicit browser
  tests. Run axe for both providers after the structural change.
- Use Bootstrap layout utilities first. Console CSS is limited to the visual work-rail skin and any
  focus treatment not already supplied by existing primitives.

## 6. Mandatory implementation sequence

Every behavioral slice is red -> green -> refactor. Because the web gate exposed a library-level
workflow gap, implement and re-pass the reusable-library behavior before changing the Razor Pages.

### Stage 0 — Accept product semantics

1. Record section 3's confirmed decisions in an ADR amending ADRs 0001, 0032, 0038, and 0044. It
   must distinguish explicit atomic composites from implicit side effects on primitive start/finish
   commands.
2. Update `jobtrack_spec_codex.md` first, then consistent supporting detail in
   `database-entities.md`, `ownership-model.md`, the API design/reference, and design language.
3. Record that the existing closed-leaf database invariant remains unchanged: the composite reaches
   a valid final state within one serialized transaction; ordinary start/set-achievement bypasses
   remain rejected.

Acceptance: the ADR is Accepted before code cites it as a requirement.

### Stage 1 — Pure semantics and public contracts

Tests first:

1. Add pure decision tests for the page/domain action matrix, exhaustive across every `Achievement`,
   archive state, active-session count, viewer participation, historical participation, node
   control, and authority.
2. Define immutable requests/results for `CompleteLeafAsync` and `ReopenAndStartWorkAsync`, including
   explicit leaf version and exact expected active-session identity/version tokens.
3. Add methods to `IWorkCommands` and the appropriate persistence port. Do not compose two
   independently committing ports in `JobTrack.Web` or `WorkCommands`.
4. Perform the required Framework Design Guidelines review and update the shipped/unshipped public
   API baselines deliberately; M6 has passed, so compatibility weight applies.
5. Add application facade tests for nulls, invalid enums, cancellation, tracing, and exception
   propagation before delegation code.

### Stage 2 — Provider command contracts and atomic implementation

Start with shared provider contract tests in `JobTrack.TestSupport`, then make PostgreSQL pass, then
SQLite, followed by provider-specific races:

1. Completing with one active session finishes it and records `Success` in one commit.
2. Completing with several workers finishes the exact set at one instant and records `Success`.
3. Completing with zero sessions succeeds only from `InProgress`.
4. A prerequisite failure, bad finish instant, unauthorized session, stale leaf version, stale
   session version, or changed active set rolls back every write.
5. Reopen-and-start performs both audited transitions and creates one session atomically for each
   terminal source state.
6. Reopen-and-start permits a prior participant to start for themselves, rejects that participant
   starting for another worker without node control, and permits a controller/manager to select an
   eligible target.
7. Reopen-and-start rolls back to the original terminal state on blocked readiness, archive,
   overlap, ineligible target, invalid time, stale version, or authorization failure.
8. Finishing one's own already-active session remains possible after node ownership changes, while
   completing the leaf or finishing another worker's session remains controller/manager-gated.
9. Primitive start and set-achievement behavior remains unchanged; the documented own-active-session
   finish exception is the only primitive authorization amendment.
10. Audit rows share the command correlation id and describe each state/session change without PII.

Use EF/LINQ for reads and writes and the existing transaction helpers. Reuse ADR 0044's PostgreSQL
per-leaf advisory lock and SQLite `BEGIN IMMEDIATE` serialization. No schema change is expected; if
a test proves one is required, follow the repository's pre-release in-place schema-script rule and
the full shared database-contract -> PostgreSQL -> SQLite -> race order.

Race tests must assert final committed state for:

- complete vs a new session start;
- complete vs another session finish;
- complete vs correction;
- reopen-and-start vs archive;
- reopen-and-start vs another reopen; and
- reopen-and-start on a former prerequisite vs dependent-job work starting.

### Stage 3 — External HTTP API and client proof

The external API already exposes session and achievement mutations, so expose the composites rather
than forcing remote clients into unsafe multi-call orchestration:

1. Add explicit endpoints, provisionally
   `POST /api/jobs/{nodeId}/complete` and
   `POST /api/jobs/{nodeId}/reopen-and-start-session`.
2. Specify optimistic-concurrency fields, expected active sessions, idempotency/retry behavior,
   conflict/problem identifiers, and cookie/PAT parity in integration tests first.
3. Update OpenAPI golden/contract tests and the external API reference.
4. Extend `samples/JobTrack.ExternalApiClient` using only HTTP/plain JSON; do not add a JobTrack
   project reference.
5. Preserve primitive endpoints for advanced clients.

### Stage 4 — Unified read model and capability projection

1. Add one bounded leaf-work-page query/projection containing node context, leaf version,
   achievement, readiness, archive state, active sessions, history page/filter state, direct
   dependent impact, and actor-specific action capabilities.
2. Reuse `ActiveSessionPresentation`; never collapse concurrent workers.
3. Prove query count is fixed with session/history growth and does not issue per-worker or per-action
   authorization queries.
4. Treat capabilities as rendering hints. Each mutation reloads and reauthorizes inside its
   transaction.

### Stage 5 — Razor interaction, tests first

Add failing integration/browser tests for every state in section 5.2, then reshape `/Jobs/Work`:

1. Build the work masthead/rail from existing Console primitives and Bootstrap layout.
2. Put achievement/readiness, active sessions, and the end-session decision above history.
3. Add complete, pause, reopen-and-start, reopen-only, and exceptional outcome handlers. Every
   successful handler follows PRG; failed validation returns the same page with the complete read
   model reloaded.
4. Replace inline `Finish / pause` mutations in Browse and Awaiting Progress with **End session**
   links into the merged page. On that page use the explicit **Pause work** and **Complete job**
   outcomes.
5. Make `/Jobs/Achievement` GET redirect to the unified status anchor and remove it as a navigable
   competing form. Preserve route compatibility and convert its existing tests into redirect and
   unified-workflow coverage; do not delete test intent.
6. Update Browse's current-leaf toolbar to lead to the unified page. Dense row eye actions may still
   deep-link to `#sessions` because Sessions remains the collection noun.
7. Use minimal `data-jt-*` progressive enhancement for disclosures, error focus, and multi-session
   review. Authorization and atomicity remain server-side.
8. Render the exact manager-escalation, archive, prerequisite, dependent-impact, and stale-state
   messages defined above.

### Stage 6 — Documentation, traceability, and visual QA

1. Update `README.md`'s operational workflow, `docs/design-language.md`, `docs/ownership-model.md`,
   `docs/database-entities.md`, API/client docs, and the traceability catalogue.
2. Update the AGENTS/CLAUDE web navigation guidance so future pages do not recreate a separate raw
   achievement form.
3. Capture screenshots at phone, tablet, and desktop for Waiting, one-active, multi-active,
   Success, unauthorized reopen, and archived+terminal states. Critique hierarchy, copy, action
   prominence, wrapping, and focus order; iterate before accepting snapshots.
4. Run axe and the end-to-end workflow against PostgreSQL and SQLite.
5. Keep this plan and `docs/plans/README.md` synchronized as stages land.

## 7. Test matrix

| Layer | Required evidence |
|---|---|
| Domain | Exhaustive action matrix; no implicit finish->success or start->reopen behavior |
| Application | Public contract validation, telemetry, cancellation, exception propagation |
| Provider contract | Atomic one/many/zero-session completion; atomic terminal reopen+start; rollback cases |
| Provider race | Active-set change, start/complete, finish/complete, archive/reopen serialize to valid state |
| Audit | One correlation, correct actor/reasons, two achievement events for reopen+start, session events |
| HTTP API | Composite endpoints, concurrency conflicts, cookie/PAT parity, OpenAPI, client proof |
| Web integration | Every state/action row, PRG, role matrix, filters, compatibility redirect, exact copy |
| Browser | End-session navigation, explicit pause/complete choice, multi-worker review, reopen form, focus, responsive layout, axe |
| Efficiency | Fixed query count; no per-session capability/directory/dependent queries |

Include Administrator, JobManager, direct/ancestor controlling Worker, prior participant who no
longer controls the node, non-participant non-controlling Worker, read-only operational employee,
Requester, disabled user, and target-worker eligibility cases. Include all three terminal
achievements and a successful leaf which currently satisfies several dependents.

## 8. Suggested commit sequence

Each commit uses the required conventional subject and a detail paragraph.

1. `docs: decide unified leaf completion and reopening semantics`
2. `test(work): specify atomic leaf completion contracts`
3. `feat(work): complete leaves with active sessions atomically`
4. `test(work): specify atomic reopen and start contracts`
5. `feat(work): reopen leaves and start sessions atomically`
6. `feat(api): expose leaf completion and reopening composites`
7. `test(web): specify unified leaf work interactions`
8. `feat(web): unify sessions and achievement workflow`
9. `docs: document the unified leaf work workflow`

Split provider commits further if needed to keep each red/green slice reviewable. Never land the
Razor composite before the reusable library operation it consumes.

## 9. Verification

For every commit, run the repository gate with every `dotnet` call outside the sandbox and every
test call under an explicit `gtimeout`:

```bash
dotnet build JobTrack.slnx -warnaserror
dotnet format JobTrack.slnx
dotnet format JobTrack.slnx --verify-no-changes
./scripts/fast-test.sh --build
```

Then run targeted filters for the affected domain/application, both provider conformance suites,
provider races, HTTP/OpenAPI/client proof, web integration, and both-provider browser tests. Run the
full solution suite once after the final stage because this changes accepted library behavior and a
central operational workflow. Clean interrupted-test databases. Commit only; never push.

## 10. Non-goals

- Deriving achievement from elapsed time or session count.
- Making pause imply success.
- Allowing primitive session start to bypass terminal/archive closure.
- Silently restoring archived nodes.
- Removing historical correction or concurrent work by different workers.
- Renaming `WorkSession`, HTTP JSON fields, database objects, or the `/Jobs/Work` route.
- Replacing Browse as the hierarchy/workflow center or the global header.
- Implementing authorization in Razor or JavaScript.

## 11. Definition of done

The work is complete only when:

1. accepted ADR/spec text settles authority, multi-session completion, archive behavior, and audit
   reasons;
2. every quick End-session action opens the merged page and requires an explicit Pause work or
   Complete job choice before mutation;
3. multi-session completion names and atomically finishes the exact reviewed set;
4. pause remains a distinct one-session action which leaves achievement unchanged;
5. an authorized actor can reopen and start in one compact workflow, with full rollback on failure;
6. an unauthorized actor sees the real escalation path rather than a doomed control;
7. `/Jobs/Work` is the only interactive status+Sessions page and `/Jobs/Achievement` safely redirects;
8. the page has one visually dominant next action in every state, remains complete without
   JavaScript, and passes responsive/keyboard/zoom/axe checks;
9. library, both providers, HTTP/OpenAPI/client proof, web, audit, concurrency, and efficiency tests
   pass; and
10. documentation, traceability, plan index, commits, and final full-suite evidence agree.
