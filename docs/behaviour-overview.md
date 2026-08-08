# Behaviour overview

How JobTrack behaves for the people using it: who may read and change what, when work may start and
finish, and what the leaf work page does. This is the narrative walk-through;
[`jobtrack_spec_codex.md`](jobtrack_spec_codex.md) is normative, [`ownership-model.md`](ownership-model.md)
holds the full read/write matrix, and `decisions/*.md` are the ADRs cited throughout.

## Who can see and change what

The short version: **anyone may look, only controllers may change** — with cost the one read that
carries its own gate.

**Reading is not ownership-gated.** Spec §7.3 gives every employee role, `Worker` included, an
unqualified "view employees and job data" baseline. Any signed-in employee can browse the whole job
tree, any node's detail, its achievement, prerequisites, readiness, and its work sessions — every
worker's, not only their own (ADR 0041). The `Requester` role is the exception: it sees only a
read-only projection of its own requests (ADR 0033).

**Writing is ownership-gated**, identically for branches and leaves (there is no branch/leaf
distinction in the rule, and ownership is inherited down the tree):

- `Administrator` and `JobManager` may manage any node — spec §7.3 gives the job manager the
  complete hierarchy.
- A `Worker` may manage only nodes they **control**: those they own directly, or that sit under an
  ancestor they own (`JobNodeAccessPolicy.CanManage`; ownership model §4, ADR 0031/0032).

Work-session recording follows node control too, not session authorship: a controlling owner may
record or correct a session for *any* worker on that node, and a Worker who controls nothing there
may record none, not even their own (`WorkSessionAccessPolicy.CanManage`). An **unassigned** node is
the exception: starting a session on one claims it for the worker the session is being recorded for,
in the same transaction, so no separate pickup step is needed (ADR 0048). Explicit `PickUpAsync`
remains the way to claim a node you are not about to start work on.

**Cost is the one gated read**, in two steps:

- `CostAccessPolicy.CanView` admits `Administrator`/`CostViewer`, **or** an owner of the node or one
  of its ancestors (ADR 0040) — so a branch you own shows its total.
- `CostAccessPolicy.CanViewNodeCost` then filters each *individual* node within it (ADR 0042): a
  branch roll-up stays visible (it is an aggregate), as does your own leaf or an unassigned one, but
  **another worker's individual leaf cost is redacted**. That figure alongside the leaf's visible
  session hours would reveal their hourly rate, which spec §7.3 reserves to the rate/cost roles.

A redacted cost simply renders blank; cost is an optional field on an otherwise browsable listing,
never a whole-request denial. When visible, actual cost is accompanied by the exact
concurrency-allocated time underlying it and is rendered as, for example, `£50.00 / 3.5 hrs`
(ADR 0053). See [`ownership-model.md`](ownership-model.md) §5.1 for the full read/write matrix.

## Prerequisites, readiness, and completion

Prerequisites are directed edges between jobs (`RequiredJob → DependentJob`); a job is **ready**
only when every prerequisite attached to it *or to any of its ancestors* is satisfied, and a
prerequisite is satisfied only once the required job's derived achievement is `Success` (spec §6,
§5). Because a prerequisite on a branch is inherited by every descendant, it gates all work in that
subtree.

Readiness is a **hard command gate**, and it applies to exactly two operations — both rechecked
live inside their own write transaction, so a prerequisite added *after* work began is still
enforced:

- **Starting** a leaf's work session is refused while the leaf is not ready.
- **Completing** a leaf (transitioning its `LeafWork` achievement into a completed state such as
  `Success`) is refused while the leaf is not ready.

Two things are deliberately **not** gated:

- **Finishing a work session** — stopping the clock only records labour that physically happened;
  the spec keeps it ungated so a prerequisite added mid-session can't trap an active worker (and the
  recorded time stays costable regardless of later prerequisite state). Finishing a session is
  distinct from completing its `LeafWork`.
- **Branch completion** — there is no "complete this branch" command to gate, because **a branch
  carries neither a stored status nor a stored cost**. Achievement is authoritative only on leaves;
  a branch's (and the root's) achievement is derived from its descendant leaves at read time — it is
  `Success` exactly when every descendant leaf has succeeded — and never written to a column (spec
  §5.2, ADR 0035), as is the Root/Branch/Leaf label itself (from `parent_id` and child existence).
  Cost is likewise never stored anywhere in the system: every cost, leaf or branch, is computed at
  query time from the actual time worked and the effective-dated rates, a branch's being the roll-up
  sum of its descendant leaves' (`HierarchicalCostAggregator`; spec §10).

The gate is enforced in the persistence layer on both providers: `StartSessionAsync`/`StartWorkAsync`
and `SetAchievementAsync` recheck readiness *inside their own write transaction* and throw
`PrerequisiteBlockedException` if the leaf is not ready — so no request routed through
`IJobTrackClient` (library, HTTP API, or web) can bypass it. (Readiness itself is recomputed at that
moment from live achievement state, not read from a cache; the database's own prerequisite triggers
enforce the edge graph's structural invariants — acyclicity, no ancestor/descendant edge — not the
readiness gate.) See spec §6 for the normative statement.

Starting a session gates on a second, independent condition: the leaf must also be **open**. A leaf
is closed to a new active session once its achievement is terminal (`Success`, `Cancelled`,
`Unsuccessful`) or its node is archived (ADR 0044) — readiness says work *may* begin;
open/closed says the leaf hasn't already been declared finished or put away. Both conditions are
checked independently and both must hold: reopening a terminal achievement back to `Waiting` is not
enough on its own if the node is also archived, and vice versa. This is enforced the same way as the
readiness gate — inside the write transaction, backstopped by a database trigger on both providers —
so a request can't reactivate a closed leaf by any route.

## Sessions, concurrent workers, and starting for others

A leaf's recorded work is presented in the browser as **Sessions** (the noun, not a renamed type —
`WorkSession`, `LeafWork`, and the `/Jobs/Work` route are unchanged). Every leaf-listing page (Browse
rows, the Awaiting Progress dashboard, and Browse's own current-leaf toolbar) shows a Sessions link
to the leaf's complete history, and reports how many workers currently have the clock running on it.

More than one worker can be actively clocked in on the same leaf at once — this is a legitimate,
supported state, not a fixable inconsistency. The UI never picks one active worker as "the" session
to show: zero active workers shows nothing, exactly one shows a compact "Active since…" pill, and two
or more show a count (`N active`) plus a capped, stable preview of who they are — the viewer's own
session first if they have one, then every other worker in start order. The complete list is always
one click away via Sessions, regardless of how many rows are capped in a dense table view.

The Awaiting Progress dashboard offers two distinct "started work" filters, which answer different
questions and are deliberately not merged. **In progress only** is about the achievement: work has
started and reached no closure, so a *paused* leaf — started, nobody clocked on — stays in. **Working
now** is about sessions: choosing an employee keeps only leaves carrying an open session of theirs,
so a paused leaf drops out and so does one someone else is working. Because starting a session
already advances the leaf to In progress (ADR 0038), choosing a name narrows past the checkbox
whether or not it is ticked. Both compose with the owner selector and the subtree scope rather than
replacing them, so "who is working what inside this subtree, right now" is one query; like every
other filter on the dashboard they are remembered per session (ADR 0052).

A user who owns a leaf (or an ancestor of it) may start a session **on behalf of another worker**
through the "Start for…" disclosure beside the ordinary one-click Start, using the same worker
picker and backdating controls everywhere else in the app. This does not change who may *view*
recorded work — session history has been visible to every employee role since ADR 0041 — only who
may create or finish a session for someone other than themselves, which remains gated by
`WorkSessionAccessPolicy.CanManage` (Administrator/JobManager unconditionally, or a Worker who
controls the leaf) and is re-checked by the command at write time regardless of what the page
rendered a moment earlier.

## The unified leaf work page

`/Jobs/Work?leafNodeId={id}` is the single interactive surface for a leaf's current status and its
Sessions (ADR 0045). It shows one obvious primary action for the current state:

- **Waiting or nothing recorded yet, no active session** — Start session (the same one-click
  `StartWorkAsync` composite described above; on an unassigned node it also claims ownership, ADR 0048).
- **In progress, no active session** — *paused*: work started and nobody is clocked on. A valid,
  ordinary state (ADR 0045 allows zero active sessions from `InProgress`) and exactly what Pause job
  produces, so it is named with a **Paused** pill wherever a leaf appears — `/Jobs/Work`, Browse's
  detail view and subtree rows, Awaiting Progress — from the single `LeafActivity.IsPaused`
  predicate. Start session resumes it; the ending decision is still offered, since completing from
  zero remaining sessions is the supported path.
- **In progress, at least one active session** — an explicit **Pause job** / **Complete job**
  decision. Pause finishes only the selected session and leaves achievement unchanged; Complete job
  atomically finishes the exact confirmed active-session set (one worker or several, all at the same
  instant) and records whichever terminal achievement its "Completion options" dropdown selects —
  `Success`, `Cancelled`, or `Unsuccessful` — in one commit (`CompleteLeafAsync`, ADR 0047, which
  supersedes ADR 0045's Success-only framing). Neither is ever implicit —
  finishing a session never silently means "done." Both share one form with the leaf's write-up and
  its own **Save write-up** button, so whichever button is pressed persists the text typed beside it.
- **A terminal leaf** (`Success`/`Cancelled`/`Unsuccessful`) — **Reopen and start session**, when the
  actor qualifies: a controlling owner, Job Manager, or Administrator may reopen and start for any
  eligible worker; a worker who recorded any previous session on that leaf may reopen and start for
  themselves only. This is `ReopenAndStartWorkAsync`, one atomic commit of the audited
  `terminal -> Waiting` transition, ADR 0038's existing `Waiting -> InProgress` auto-advance, and the
  new session — amending ADR 0001's original Administrator/JobManager-only reopen rule for this
  composite path specifically (an isolated reopen with no session following it stays
  Administrator/JobManager-only, in "Change outcome" below).
- **Archived** — no active-session action at all; the page names the restore requirement instead of
  silently reactivating a closed node.

A single "Change outcome" dropdown covers every remaining transition — each one
`AchievementTransitions.IsPermitted` allows from the current state, filtered to what the actor is
authorized for, including reopening without starting a session — through the original
`SetAchievementAsync` primitive, unchanged.

`/Jobs/Achievement`, the page's now-retired predecessor, is a compatibility redirect to
`/Jobs/Work#status`; nothing links to it directly any more.

### Who can pause, complete, and resolve a paused leaf

Two distinct authorization rules govern a leaf's work sessions, and they're deliberately not the
same rule:

- **Finishing (pausing) your own session needs no ownership at all.** `WorkSessionAccessPolicy.CanFinishSession`
  ([`src/JobTrack.Domain/Authorization/WorkSessionAccessPolicy.cs:70-75`](../src/JobTrack.Domain/Authorization/WorkSessionAccessPolicy.cs#L70-L75))
  grants finish authority to `CanManage` (below) **or** simply `Worker role && isOwnSession` — the
  ADR 0045 §5 exception that lets a worker always stop their own clock, even if node ownership moved
  elsewhere after they started. It grants pause authority only, nothing more. The command port checks
  it per session at [`SqliteWorkSessionCommandPort.cs:857-869`](../src/JobTrack.Persistence.Sqlite/SqliteWorkSessionCommandPort.cs#L857-L869).
- **Starting a session, correcting one, or completing the leaf all require node control.**
  `WorkSessionAccessPolicy.CanManage`
  ([`WorkSessionAccessPolicy.cs:23-28`](../src/JobTrack.Domain/Authorization/WorkSessionAccessPolicy.cs#L23-L28))
  and `AchievementAccessPolicy.CanSetAchievement`
  ([`src/JobTrack.Domain/Authorization/AchievementAccessPolicy.cs:15-24`](../src/JobTrack.Domain/Authorization/AchievementAccessPolicy.cs#L15-L24))
  both resolve to the same rule: **Administrator or JobManager unconditionally, or a Worker who
  controls the leaf** (owns it directly or via an owned ancestor). `CompleteLeafAsync` checks this
  once per call at [`SqliteWorkSessionCommandPort.cs:447`](../src/JobTrack.Persistence.Sqlite/SqliteWorkSessionCommandPort.cs#L447)
  (via `AuthorizeCompleteOrThrowAsync`,
  [`:692-702`](../src/JobTrack.Persistence.Sqlite/SqliteWorkSessionCommandPort.cs#L692-L702)) — an
  ordinary worker with no ownership stake cannot complete the leaf even if they did all the work on
  it.

**A leaf that ends up paused — `LeafWork.Achievement == InProgress` with zero active sessions
(`LeafActivity.IsPaused`, [`src/JobTrack.Web/LeafActivity.cs:22-23`](../src/JobTrack.Web/LeafActivity.cs#L22-L23))
— is not a dead end.** A worker with no control over the node can trivially reach this state (they
paused their own, only session, per the exception above), but resolving it — starting a new session
or completing the leaf — needs `CanManageSessions`/`CanComplete`, so it falls to a controlling owner,
JobManager, or Administrator. Because Administrator/JobManager authority is unconditional in both
policies (no ownership check at all), there's always someone who can move it forward regardless of
who owns the node. `/Jobs/Work` reflects this directly: with no active session and no completion
authority, the actor sees "A controlling owner, Job Manager, or Administrator can start work on this
job" ([`Work.cshtml:156`](../src/JobTrack.Web/Pages/Jobs/Work.cshtml#L156)) instead of Start session,
and the ending decision itself is gated by `hasEndingSection`/`workPage.CanComplete`
([`Work.cshtml:14-15`](../src/JobTrack.Web/Pages/Jobs/Work.cshtml#L14-L15),
[`:182`](../src/JobTrack.Web/Pages/Jobs/Work.cshtml#L182)), computed server-side in
[`JobQueries.cs:669-677`](../src/JobTrack.Application/JobQueries.cs#L669-L677).

**"Finish N sessions and complete job" uses the same single completion check, not one per session.**
`CompleteLeafAsync` authorizes once against the node
([`SqliteWorkSessionCommandPort.cs:447`](../src/JobTrack.Persistence.Sqlite/SqliteWorkSessionCommandPort.cs#L447)),
then finishes every currently active session on the leaf in the same transaction
([`:482-491`](../src/JobTrack.Persistence.Sqlite/SqliteWorkSessionCommandPort.cs#L482-L491)) with no
check that the actor is the worker on each one. A controlling owner (or JobManager/Administrator)
closing out a job therefore also ends every other worker's active session on it as a side effect —
intentional per ADR 0045 §5, not a gap: the self-finish exception governs pausing one's own session
only and never extends to `CompleteLeafAsync`.

## Deleting a subtree

Two different deletions exist, and Browse offers exactly one of them per node so the button label
always states the scope before it is clicked.

**One job** — `/Jobs/Delete`, offered on a job with no children. It never cascades: a job with
children, a prerequisite edge, or the permanent root is refused (ADR 0036). A leaf whose work was
never actually done deletes with its `LeafWork`; a leaf with real session history needs the
Administrator role and a reason.

**A whole branch** — `/Jobs/DeleteSubtree`, offered only to an Administrator, and only on a job that
has children (ADR 0061). It permanently destroys the job, every job beneath it, and their sessions,
rate overrides, and requests, in one transaction that is never partially applied. A reason is always
required. Before confirming, the page lists exactly what will go: how many jobs and sessions, how
much recorded work, what it cost, which dependency links break, and every job by name — drawn as the
same indented tree Browse uses, with each job's own rolled-up cost beside it and the subtree total in
the summary above. Costs are supplementary context, so if a rate no longer resolves (or the viewer
cannot see costs) that one panel says so and the rest of the confirmation still works.

Three things are worth knowing about it:

- **Reported costs move.** Cost is computed live from current sessions and never snapshotted, so
  destroying a branch's history changes what every job above it reports from then on. Only the audit
  event records what was there.
- **Dependency links are dropped, not refused.** A job outside the branch that was waiting on
  deleted work loses its prerequisite and may become ready — a valid state per ADR 0051, and the
  confirmation names each such job first.
- **It refuses exactly one thing.** A request holding area anchored inside the branch aborts the
  deletion, because that is a department's intake configuration rather than this branch's own data.
  Re-anchor or deactivate it first.

**Archive instead** sits beside the delete button on the same page and is always available,
including on the permanent root. It marks the branch and everything under it archived without
destroying anything, and refuses only while a session is still running somewhere inside.

## External HTTP API

Beyond the server-rendered Razor Pages, `JobTrack.Web` exposes a resource-oriented JSON API under
`/api/*` for clients that aren't on the same trusted host.
[`api/external-http-api-reference.md`](api/external-http-api-reference.md) is the full route table,
auth model, and request/response examples;
[`plans/2026-07-09-external-http-api-plan.md`](plans/2026-07-09-external-http-api-plan.md) and ADRs
0024, 0029, 0030 record the design decisions and rationale behind it. In brief:

- **Authentication** — either the browser's cookie session, or an opaque bearer personal access
  token (PAT) for non-browser clients. A PAT authenticates strictly as its issuing user and is
  revoked automatically on disablement, role changes, and password reset/change, alongside that
  user's web sessions.
- **Surface** — read-only job-tree browsing and search, work sessions (start/finish/correct/list —
  a UI "resume"/"pause"/"stop" is the same start/finish command, not a separate endpoint),
  prerequisites and achievement, and cost reports. Structural job commands, audit browsing, and
  account administration remain Razor-Pages-only for now (ADR 0030) — this is a deliberately scoped
  surface, not a mechanical mirror of every browser workflow.
- **Operational qualities** — per-user rate limiting distinct from browser login throttling, and
  bounded per-request telemetry (operation, correlation id, status, duration, stable failure
  category) that never carries a rate/cost value or a token.
- **Client proof** — `samples/JobTrack.ExternalApiClient` is a small first-party CLI client with
  **no project reference to any `JobTrack.*` library assembly**: it talks only to the published
  HTTP contract, proving the API is genuinely usable from outside the reusable .NET library.
  `tests/JobTrack.Web.EndToEndTests/ExternalApiClientProofTests.cs` drives it against both
  providers, exercising authentication, a read workflow, a mutation workflow, conflict handling,
  and revocation handling.
