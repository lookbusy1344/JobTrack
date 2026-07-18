# `IJobTrackClient` public API design

**Closes:** Implementation plan §7.1 ("write consumer-first API specifications and compiling usage
examples before creating implementations"). Reviewed against the monorepo-root
`Framework_Design_Guidelines_Essentials.md` (one level above this repository, shared across
`VSStuff/`'s projects — see top-level `CLAUDE.md`) and `jobtrack_spec_codex.md` §13.2. This document
records the *design*, including the parts not yet implemented; `git log`/the current source tree is
authoritative for what actually exists at any given moment (see the status table below).

## Why the facade grows additively instead of being declared complete up front

`jobtrack_spec_codex.md` §13.2 asks for "one configured entry point... which groups cohesive job,
work-session, schedule, rate, query, audit, and costing capabilities." A literal reading would
declare all seven sub-interfaces in `IJobTrackClient` today, before any of their members are
implemented. Two things in this repository's own configuration rule that out:

1. **`AnalysisModeDesign=All`** (`Directory.Build.props`) enables CA1040 ("avoid empty
   interfaces"). A sub-interface with no members yet is a build error under this project's
   `-warnaserror` gate, not a style suggestion.
2. **CLAUDE.md's "no half-finished implementations"** rule applies as much to a public contract as
   to any other code: an interface property that exists only to be filled in later is exactly the
   half-finished shape that rule prohibits.

So `IJobTrackClient` ships with exactly the sub-interfaces whose slice (plan §7.3) has actually been
designed and declared, and grows one property at a time as each slice lands. Per ADR 0013, adding a
property to `IJobTrackClient` is additive evolution, not a breaking change, because the facade is
implemented only inside this repository (by the persistence assemblies) and consumers (`JobTrack.Web`,
`JobTrack.AdminCli`) only ever call it, never implement it themselves — see
`Framework_Design_Guidelines_Essentials.md` Appendix D for why that distinction matters. The table
below is the target shape; the Status column reflects what exists in code today.

| Property | Sub-interface | Plan §7.3 step | Status |
|---|---|---|---|
| `Installation` | `IInstallationCommands` | 1 (bootstrap) | **Implemented** |
| `Jobs` | `IJobCommands` | 3–5 (planning nodes, leaf work, prerequisites) | **Implemented** |
| `Work` | `IWorkCommands` | 6 (sessions), 7 (achievement) | **Implemented** |
| `Schedules` | `IScheduleCommands` | 8 (schedule versions/exceptions) | **Implemented** |
| `Rates` | `IRateCommands` | 9 (user rates/node overrides) | **Implemented** |
| `Query` | `IJobQueries` | 2, 5 (profiles, readiness, hierarchy) | **Implemented** — grew well past steps 2/5 below: job-tree browsing/search/summaries, awaiting-progress, work-session listing, leaf work, and prerequisite-edge queries were all added for `JobTrack.Web`'s browsing UI (plan §8.5 slice 2) and the external HTTP API (`docs/plans/2026-07-09-external-http-api-plan.md`) — see `src/JobTrack.Application/IJobQueries.cs` for the authoritative current member list, not the code block below |
| `Costing` | `ICostQueries` | 10 (cost details, totals) | **Implemented** |
| `Audit` | `IAuditQueries` | 11 (audit search) | **Implemented** |

"Rate" is broken out from "Schedule" as its own property even though `jobtrack_spec_claude.md`
§12.2 groups them under one `IScheduleCommands` — the implementation plan's own §7.1 bullet list
names "job, work, schedule, **rate**, audit, and costing" as six distinct capability areas, and
plan §7.3 gives rates their own step (9) separate from schedules (8). The plan is this project's
more specific, more current source and wins over the secondary spec's grouping (per
`docs/decisions/0004-specification-precedence.md`-style precedence, applied here to the two spec
documents' own disagreement, not just spec-vs-plan).

## Cross-cutting shape, applied to every command/query below

- **Async, cancellable, task-based**: every member is `Task`/`Task<T>`-returning, `Async`-suffixed,
  with a trailing `CancellationToken cancellationToken = default` — the only defaulted parameter
  anywhere in the surface (FDG ch. 8).
- **Immutable contracts**: every request and result is a `sealed record` with `required`/`init`
  members. No mutable concrete collections in a signature — `IReadOnlyList<T>`/`IEnumerable<T>` in,
  `IReadOnlyList<T>` out, never `null` for "no results" (empty collection instead).
- **Strongly typed identifiers and value objects** (`AppUserId`, `JobNodeId`, `Money`,
  `HourlyRate`, ...) from `JobTrack.Abstractions`, never a bare `long`/`decimal` where a specific ID
  or amount is meant (ADR 0006).
- **No Boolean clusters**: an enum parameter where two or more related booleans would otherwise be
  needed together (FDG ch. 5).
- **Optimistic concurrency**: every mutation request that targets an existing row carries the
  caller's `Version` (a `long`); every mutation result returns the new `Version`. A stale version
  throws `ConcurrencyConflictException` (`JobTrack.Abstractions`), never a silent overwrite.
- **Actor and correlation context**: every command and query except
  `IInstallationCommands.BootstrapAdministratorAsync` (which by definition precedes any actor's
  existence) carries a shared `CommandContext { AppUserId Actor; Guid CorrelationId; }`
  (`src/JobTrack.Application/CommandContext.cs`) — decided when `IJobQueries` (plan §7.3 step 2)
  was implemented, reused uniformly for reads and writes rather than a separate read-only context
  type.
- **Exceptions are the sole failure channel** (ADR 0019): usage errors throw framework exceptions
  directly; the six `JobTrackException` subtypes in `JobTrack.Abstractions`
  (`EntityNotFoundException`, `AuthorizationDeniedException`, `ConcurrencyConflictException`,
  `PrerequisiteBlockedException`, `MissingRateException`, `InvariantViolationException`) cover every
  condition callers handle distinctly. No `Try*` member exists anywhere in this design yet — none
  has a measured performance or common-failure justification (ADR 0019's relief-valve criterion).
- **Mutation authorization is port-owned; query authorization is Application-owned:** command
  handlers (`JobCommands`, `WorkCommands`, `RateCommands`, `ScheduleCommands`, `EmployeeCommands`,
  `RequestCommands`, `TokenCommands`) orchestrate tracing and input shaping only — they never call
  domain `*AccessPolicy` types. Each persistence command port reloads the actor's roles (and any
  ownership or eligibility facts the policy needs) inside its own transaction and throws
  `AuthorizationDeniedException` before a write lands. Query handlers are the opposite: ports return
  actor roles (and, where needed, scoped payloads); Application applies `*AccessPolicy`, and for
  expensive or cross-scope reads (cost, employee profile, audit, sessions) checks authorization
  **before** invoking heavy port methods. Provider contract tests are the per-port enforcement
  evidence; `JobTrack.ArchitectureTests` asserts command handlers stay free of direct policy calls.

## `IInstallationCommands` — implemented

```csharp
public interface IJobTrackClient
{
    IInstallationCommands Installation { get; }
}

public interface IInstallationCommands
{
    Task<BootstrapAdministratorResult> BootstrapAdministratorAsync(
        BootstrapAdministratorRequest request, CancellationToken cancellationToken = default);
}
```

`BootstrapAdministratorRequest` carries no actor (none exists yet) and no `Version` (nothing to
compare-and-swap against yet) — the one request in the whole surface without either, which is why
it is called out explicitly above rather than left to the general rule. See
`src/JobTrack.Application/BootstrapAdministratorRequest.cs` and `BootstrapAdministratorResult.cs`
for the exact fields, and
`tests/JobTrack.PublicApi.Tests/JobTrackClientUsageExampleTests.cs` for the compiling usage example
plan §7.1 asks for — a consumer calling `client.Installation.BootstrapAdministratorAsync(...)`
against a fake implementation, including the "already initialised" failure path
(`InvariantViolationException` with `ConstraintId` `"installation-already-initialised"`, per
ADR 0015).

## `IJobQueries` — steps 2 and 5 (employee profile/account state; readiness) implemented

```csharp
public interface IJobTrackClient
{
    IInstallationCommands Installation { get; }
    IJobQueries Query { get; }
}

public interface IJobQueries
{
    Task<EmployeeProfileResult> GetEmployeeProfileAsync(
        GetEmployeeProfileRequest request, CancellationToken cancellationToken = default);

    Task<AccountStateResult> GetAccountStateAsync(
        GetAccountStateRequest request, CancellationToken cancellationToken = default);

    Task<ReadinessResult> GetReadinessAsync(
        GetReadinessRequest request, CancellationToken cancellationToken = default);
}
```

The employee queries authorize via `JobTrack.Domain.Authorization.EmployeeAccessPolicy.CanViewEmployee`:
the actor may view their own record, or any employee's if the actor holds `EmployeeRole.Administrator`
— reloaded fresh from the persistence-owned `IEmployeeQueryPort` on every call rather than trusting
a cached claim (plan §7.5). `AccountStateResult` carries account state and role assignments only,
never a password hash or security stamp (spec §16). See
`src/JobTrack.Application/JobQueries.cs`, `IEmployeeQueryPort.cs`, and
`tests/JobTrack.Application.Tests/JobQueriesTests.cs` for the fake-port application tests; provider
conformance tests land with the persistence slice (§7.4).

`GetReadinessAsync` returns `JobTrack.Domain.Hierarchy.ReadinessResult` directly rather than an
Application-owned clone — it is already an immutable, dependency-free value type, and duplicating
it field-for-field would be pure ceremony. It carries **no** ownership-based authorization gate,
unlike the employee queries above: spec §7.3 lists viewing job data as an unqualified baseline
capability for every role (Worker: "View employees and job data"; Auditor: "Read job, work,
schedule, and audit information without mutation"), unlike employee-account data, which is
Administrator/self-only. The persistence-owned `IReadinessQueryPort` materializes every fact the
pure `ReadinessCalculator` needs — the target node and its ancestors, every prerequisite declared on
any of them, and the complete subtree of each required job (achievement derivation is recursive) —
and `JobQueries` calls the calculator directly, doing no graph traversal itself. Remaining hierarchy
queries (subtree listing, direct achievement lookup) are added to this same interface as later
slices land.

## `IJobCommands` — steps 3–5 (planning nodes, leaf work, prerequisites) implemented

```csharp
public interface IJobTrackClient
{
    IInstallationCommands Installation { get; }
    IJobQueries Query { get; }
    IJobCommands Jobs { get; }
}

public interface IJobCommands
{
    Task<JobNodeResult> AddChildAsync(CreateJobNodeRequest request, CancellationToken cancellationToken = default);
    Task<JobNodeResult> EditAsync(EditJobNodeRequest request, CancellationToken cancellationToken = default);
    Task<JobNodeResult> MoveAsync(MoveJobNodeRequest request, CancellationToken cancellationToken = default);
    Task<JobNodeResult> ArchiveAsync(ArchiveJobNodeRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(DeleteJobNodeRequest request, CancellationToken cancellationToken = default);
    Task<LeafWorkResult> AttachLeafWorkAsync(AttachLeafWorkRequest request, CancellationToken cancellationToken = default);
    Task<DecomposeWorkedLeafResult> DecomposeWorkedLeafAsync(
        DecomposeWorkedLeafRequest request, CancellationToken cancellationToken = default);
    Task AddPrerequisiteAsync(AddPrerequisiteRequest request, CancellationToken cancellationToken = default);
    Task RemovePrerequisiteAsync(RemovePrerequisiteRequest request, CancellationToken cancellationToken = default);
}
```

`AddChildAsync` creates an ordinary child node under a parent. Whether it later appears as a branch
or leaf depends on structure: attaching `LeafWork` keeps it a leaf; adding children makes it a
branch. `JobNodeResult.Kind` is a derived contextual label, not stored state. Every mutation authorizes via
`JobTrack.Domain.Authorization.JobNodeAccessPolicy.CanManage` (Administrator or JobManager always;
Worker only if they own the target node/parent or one of its ancestors, per spec §7.3), but unlike
the read-only `IJobQueries` ports, the check happens **inside** the persistence-owned
`IJobNodeCommandPort`'s own transaction, not in `JobCommands` afterward — a mutation cannot safely
hand authorization facts back to the caller to decide once the write has already landed, the same
shape as the bootstrap port's own "already initialised" guard (ADR 0005, ADR 0015). `MoveAsync`
authorizes against the node being moved, not the destination parent: an assigned node owner, an
owner of one of that node's ancestors, JobManager, or Administrator may move it to any otherwise
valid parent. This supports moving requester jobs out of holding areas and re-homing larger jobs as
their structure becomes clearer without introducing a second destination-owner approval rule. The
move still rejects structural or workflow-invalid destinations, including moving the root, moving a
node under itself or its descendant, leaving a node with both `LeafWork` and children, or violating
the prerequisite ancestor/descendant rule after re-parenting
(`InvariantViolationException`, stable constraint id such as `"job-node-move-would-cycle"` for the
cycle case). `DeleteAsync` is
deletion of a proven-unused planning node only (spec §3.6); a node with dependent data throws
`InvariantViolationException` with `ConstraintId` `"job-node-not-deletable"` — everything else is
archived, never deleted.

`AttachLeafWorkAsync` (step 4) attaches achievement tracking to an existing childless node created by
`AddChildAsync`; every new `LeafWork` starts at `Achievement.Waiting` (changing it is step 7's
`SetAchievementAsync`, not this command). It throws `InvariantViolationException` with
`ConstraintId` `"job-node-has-children-cannot-attach-leaf-work"` if the target already has children,
or `"leaf-work-already-attached"` if one is already attached — both mirror the leaf/branch
exclusivity already enforced by schema version 0006's deferred constraint triggers. `DecomposeWorkedLeafAsync` (spec §3.5) atomically
creates a child inheriting the existing `LeafWork` (and, once work sessions exist in §7.3 step 6,
every session unchanged), creates each newly identified child in `NewChildren`, and converts the
original leaf into their branch parent; it throws `InvariantViolationException` with `ConstraintId`
`"leaf-work-not-attached"` if there is no existing `LeafWork` to decompose. Unlike the design
sketch that originally stood in for this slice (a bare `Task`), it returns the new identifiers the
caller needs to reference what was just created. See `src/JobTrack.Application/JobCommands.cs`,
`IJobNodeCommandPort.cs`, and `tests/JobTrack.Application.Tests/JobCommandsTests.cs` for the
fake-port application tests; provider conformance tests — including the real acyclicity/lock
mechanics already established by schema versions 0004/0005/0006, and the concurrency/rollback
testing the plan flags this step for — land with the persistence slice (§7.4).

`AddPrerequisiteAsync`/`RemovePrerequisiteAsync` (step 5, spec §6) manage `job_prerequisite` edges
(`RequiredJobId` must reach derived `Achievement.Success` before `DependentJobId` is ready). Both
require the actor to be authorized for **both** endpoints' subtrees, the same dual-check shape as
`MoveAsync`. `AddPrerequisiteAsync` throws `InvariantViolationException` with `ConstraintId`
`"job-prerequisite-not-self"` (a job cannot require itself), `"job-prerequisite-is-hierarchy-edge"`
(endpoints already ancestor/descendant — schema version 0008's rule 5),
`"job-prerequisite-already-exists"` (schema's composite-key duplicate rule), or
`"job-prerequisite-would-cycle"` (schema version 0008's rule 4) — every one mirroring an invariant
that schema version 0008 already enforces at the database layer; the port's fake test double
implements the same graph checks in memory so the behaviour is provable before any persistence
provider exists. Neither method returns a result beyond confirming success: a `job_prerequisite` row
has no server-generated fields or independent version to report. See
`src/JobTrack.Application/JobCommands.cs`, `IJobNodeCommandPort.cs`, and
`tests/JobTrack.Application.Tests/JobCommandsTests.cs`.

## `IWorkCommands` — steps 6–7 (work sessions; achievement) implemented

```csharp
public interface IJobTrackClient
{
    IInstallationCommands Installation { get; }
    IJobQueries Query { get; }
    IJobCommands Jobs { get; }
    IWorkCommands Work { get; }
}

public interface IWorkCommands
{
    Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default);
    Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default);
    Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default);
    Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default);
}
```

Only three operations exist — spec §4.4 is explicit that "pause", "stop", and "resume" are UI
descriptions, not distinct domain operations: pause/stop is `FinishSessionAsync` (setting
`FinishedAt`), and resume is `StartSessionAsync` called again for the same leaf and worker.
Neither `StartSessionRequest` nor `FinishSessionRequest` carries a caller-supplied instant — each
command captures one clock value ("now") itself (plan §2), leaving `CorrectSessionRequest` as the
sole path for a caller-supplied time, exactly matching spec §4.4's "historical correction" framing.

Authorization uses a new `JobTrack.Domain.Authorization.WorkSessionAccessPolicy.CanManage`:
Administrator or JobManager may manage any session; a Worker only their own (spec §4.4: "Workers
may correct their own historical sessions... Job managers and administrators may correct any
session"). The check happens inside `IWorkSessionCommandPort`'s own transaction, the same
mutation-safety shape as `IJobNodeCommandPort`. `StartSessionAsync` additionally rechecks the
leaf's prerequisite readiness inside that same transaction and throws `PrerequisiteBlockedException`
if unsatisfied (spec §6: "a new WorkSession cannot be started for the dependent job"; "the start
and completion commands shall recheck prerequisites inside their write transaction") — reusing the
same `ReadinessCalculator` as `IJobQueries.GetReadinessAsync`, not a separate check. It throws
`InvariantViolationException` with `ConstraintId` `"work-session-already-active"` if the worker
already has an unfinished session for the leaf (schema version 0007's partial unique index).
`FinishSessionAsync` has no readiness gate: "an existing session may still be paused or finished so
prerequisite regression cannot trap an active session" (spec §6). `CorrectSessionAsync` throws
`InvariantViolationException` with `ConstraintId` `"work-session-invalid-interval"` (finish must be
after start) or `"work-session-overlap"` (would overlap another session for the same worker and
leaf — schema version 0007's exclusion constraint), mirroring the database-layer invariants exactly.
See `src/JobTrack.Application/WorkCommands.cs`, `IWorkSessionCommandPort.cs`, and
`tests/JobTrack.Application.Tests/WorkCommandsTests.cs` for the fake-port application tests
(`FakeWorkSessionCommandPort` delegates leaf existence and readiness to the shared
`FakeJobNodeCommandPort` graph rather than duplicating it); provider conformance tests — including
the same-user/same-leaf overlap exclusion constraint and one-active-session partial index — land
with the persistence slice (§7.4).

`SetAchievementAsync` (step 7, ADR 0001) transitions a leaf's `LeafWork` achievement through the
canonical state machine — `Waiting -> InProgress -> {Success, Cancelled, Unsuccessful}`,
`Waiting -> {Cancelled, Unsuccessful}` directly, and any terminal state reopenable back to
`Waiting` — implemented as a new pure `JobTrack.Domain.Hierarchy.AchievementTransitions`, unit
tested directly rather than only through the command. It returns `LeafWorkResult`, not the
illustrative sketch's `JobNodeResult`, since achievement lives on `LeafWork`, not `JobNode`.
Authorization is a new `JobTrack.Domain.Authorization.AchievementAccessPolicy`: an ordinary forward
transition uses the same subtree-ownership rule as every other job-node command
(`JobNodeAccessPolicy.CanManage`), but reopening a terminal state back to `Waiting` requires
`EmployeeRole.Administrator` or `EmployeeRole.JobManager` regardless of ownership — a Worker may
never reopen their own work (ADR 0001: "Reopening authority"). It throws
`InvariantViolationException` with `ConstraintId` `"achievement-transition-not-permitted"` for any
transition outside the permitted graph, and — for a transition into any of the three terminal
states — rechecks the leaf's prerequisite readiness inside the same transaction, reusing the same
`ReadinessCalculator` as `GetReadinessAsync`/`StartSessionAsync`, throwing
`PrerequisiteBlockedException` if unsatisfied (spec §6: "the dependent `LeafWork` cannot transition
to `Success` or another completed state" while a prerequisite is unsatisfied — this applies to
every terminal state, not only `Success`, per both spec documents' identical wording). See
`src/JobTrack.Domain/Hierarchy/AchievementTransitions.cs`,
`src/JobTrack.Domain/Authorization/AchievementAccessPolicy.cs`, `IAchievementCommandPort.cs`, and
the corresponding test suites.

## `IScheduleCommands` — step 8 (schedule versions/exceptions) implemented

```csharp
public interface IJobTrackClient
{
    IInstallationCommands Installation { get; }
    IJobQueries Query { get; }
    IJobCommands Jobs { get; }
    IWorkCommands Work { get; }
    IScheduleCommands Schedules { get; }
}

public interface IScheduleCommands
{
    Task<ScheduleVersionResult> AddScheduleVersionAsync(
        AddScheduleVersionRequest request, CancellationToken cancellationToken = default);

    Task<ScheduleExceptionResult> AddScheduleExceptionAsync(
        AddScheduleExceptionRequest request, CancellationToken cancellationToken = default);
}
```

Both requests reuse pure `JobTrack.Domain.Schedules` values directly (`ScheduleVersion`,
`ScheduleExceptionEntry`) rather than re-declaring their fields as flat Application DTOs — each
domain type's own constructor already enforces its structural invariants (effective-end strictly
after effective-start, each `WeeklyInterval` individually well-formed, a rate override only ever on
an `AddWorkingTime` exception), so a request built from one is never structurally invalid before it
reaches the port. Neither method returns anything a caller couldn't already query back — added here
so the caller has the persisted identifier and concurrency version.

Authorization is a new `JobTrack.Domain.Authorization.ScheduleAccessPolicy.CanManage`:
`EmployeeRole.Administrator`, or the schedule is the actor's own — spec §7.3 is explicit that
"Workers may edit their own schedules and exceptions, but not another employee's," and,
distinctively among the policies built so far, `EmployeeRole.JobManager` is **not** granted an
override here (unlike job-node, work-session, and achievement commands) since neither spec
document lists schedule management under Job manager's authority. `AddScheduleVersionAsync` throws
`InvariantViolationException` with `ConstraintId` `"schedule-version-overlap"` if the new version's
effective range overlaps another of the employee's versions (schema version 0009's exclusion
constraint). `AddScheduleExceptionAsync` throws `InvariantViolationException` with `ConstraintId`
`"schedule-exception-priced-additive-overlap"` only when both the new and an existing exception are
priced `AddWorkingTime` exceptions whose intervals overlap (schema version 0010's partial exclusion
constraint) — unpriced additive exceptions, and all subtractive exceptions, may overlap freely
(spec §8.3). See `src/JobTrack.Application/ScheduleCommands.cs`, `IScheduleCommandPort.cs`, and
`tests/JobTrack.Application.Tests/ScheduleCommandsTests.cs` for the fake-port application tests;
provider conformance tests land with the persistence slice (§7.4).

## Rates, Costing, and Audit: now implemented — see source, not this historical sketch

The code block this section used to carry (illustrative, pre-implementation member shapes for
`IRateCommands`/`ICostQueries`/`IAuditQueries`) is deleted rather than corrected in place: every
name in it was superseded during implementation (e.g. `SetUserCostRateAsync` became
`IRateCommands.AddUserCostRateAsync`; `GetCostBreakdownAsync`/`GetCostOfNodeAsync` became
`ICostQueries.GetCostDetailsAsync`/`GetHierarchyTotalsAsync`), so keeping the old sketch next to
the real interfaces invited exactly the confusion this document's own opening paragraph warns
about. For the authoritative current shape of each, read:

- `src/JobTrack.Application/IRateCommands.cs`
- `src/JobTrack.Application/ICostQueries.cs` (also documented for HTTP consumers in
  `docs/api/external-http-api-reference.md`)
- `src/JobTrack.Application/IAuditQueries.cs`

`asOf`-style "as of an instant" parameters across the surface follow the same rule described above
for every other command: `null` means "now", captured once inside the command (plan §2's "one
captured clock value per operation").

## Registration (not yet designed in detail)

Spec §13.2 / spec_claude §12.8 call for parallel composition methods
(`AddJobTrackPostgreSql`/`AddJobTrackSqlite`) that wire a provider without exposing connections or
repositories. This is deferred to plan §7.4 (persistence implementations), since it cannot be
designed sensibly before at least one persistence provider exists to compose.
