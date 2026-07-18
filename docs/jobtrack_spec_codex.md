# JobTrack Modernisation Specification

**Status:** Draft 3  
**Date:** 2026-07-04  
**Authority:** This is the **primary, authoritative specification** for JobTrack. `jobtrack_spec_claude.md` is the **secondary** specification and supplies implementation detail only where consistent with this document; any conflict is resolved in favour of this specification.  
**Primary platform:** PostgreSQL with SQLite portability, .NET 10 library, ASP.NET Core

## 1. Purpose

JobTrack records hierarchical jobs, their prerequisites, actual work, achievement, schedules, labour rates, and dynamically calculated costs.

This specification defines the target behaviour for replacing the legacy SQL Server database and application with:

- PostgreSQL (latest stable version) as the primary and authoritative datastore;
- SQLite (latest stable version) as a supported secondary datastore for embedded or single-node deployments, designed alongside PostgreSQL from the first database phase;
- a reusable .NET 10 JobTrack library which owns domain behaviour and persistence; and
- a modern ASP.NET Core application targeting .NET 10 as the primary front end, with other front ends able to consume the same library.

PostgreSQL is the product's primary datastore and reference implementation for production behaviour and performance. SQLite portability is nevertheless a first-order design constraint from the beginning of the database phase. Every schema concept, invariant, query contract, migration, and transaction workflow shall have a practical SQLite implementation before it is accepted into the design. Provider-specific enforcement is allowed, but the common domain model shall not depend on PostgreSQL-only semantics which are difficult or fragile to reproduce in SQLite. Conformance tests must demonstrate equivalent observable results and failures.

The reusable library is the product core. The ASP.NET Core application is its primary interface and shall provide the main browser UI and HTTP API. Other front ends, such as command-line tools, workers, batch reporting, or future desktop clients, may consume the same library directly. No front end owns domain rules or accesses JobTrack tables directly.

Delivery order is strict: first design and validate the PostgreSQL and SQLite databases; then design and implement the reusable library against those database contracts; then build the ASP.NET Core front end. A later phase may expose deficiencies in an earlier one, but it shall feed changes back through the earlier specification and tests rather than bypassing its boundary.

## 2. Scope

The modernised system shall support:

- a single hierarchy of job nodes;
- leaf execution records and repeatable work sessions representing actual work;
- prerequisite relationships between jobs;
- recursively derived achievement state;
- user working schedules and dated schedule exceptions;
- effective-dated labour rates and inherited job-specific rate overrides;
- dynamic allocation of labour cost across concurrent work;
- historical auditing; and
- reliable enforcement of all structural and temporal invariants.

The system shall not treat a job node or leaf execution record as evidence that work occurred. Only a `WorkSession` represents an interval of actual work.

### 2.1 Deliberate change from the legacy model

The legacy database gives a leaf `JobNode` at most one `WorkRecs` row. Pausing or continuing work is represented by restructuring the job tree: the existing record is moved to a newly created child and another child represents the continuation.

The modern model deliberately replaces that behaviour with two concepts:

- `LeafWork` stores the execution and achievement state of a leaf job; and
- `WorkSession` stores one continuous interval worked by one user.

A `LeafWork` may have multiple `WorkSessions`. Pausing finishes the current session and resuming creates another session without changing the job hierarchy. Tree restructuring is reserved for genuine decomposition, such as splitting “Fix feature A” into “Fix A1” and “Fix A2”. This distinction is fundamental to the target design and shall not be collapsed back into the legacy one-row model.

## 3. Terminology

| Term | Meaning |
|---|---|
| Job hierarchy | The single rooted tree formed by `JobNode` parent relationships. |
| Root | The sole `JobNode` without a parent. It is structural and cannot contain `LeafWork`. |
| Branch | A `JobNode` with one or more direct children and no `LeafWork`. |
| Leaf | A `JobNode` with no children. It may contain zero or one `LeafWork`. |
| Leaf work | The execution, completion criteria, and achievement state belonging to one leaf node. |
| Work session | One continuous interval during which one user performed or is performing actual work for one `LeafWork`. |
| Prerequisite | A directed relationship declaring that one job must succeed before another is eligible to proceed. |
| Success | The only achievement outcome that satisfies a prerequisite. |
| Working interval | A time interval during which a user is normally considered available for cost calculation. |
| Schedule exception | A dated interval that adds to or removes from a user's normal working intervals. |
| Active work | A `WorkSession` whose interval contains the instant being costed. |

Unless explicitly stated otherwise, all time intervals are half-open: `[start, end)`. An interval includes its start and excludes its end.

## 4. Core domain model

### 4.1 JobNode

A `JobNode` represents a unit of organisation in the job hierarchy. It may represent either a job containing subtasks or a leaf at which work can be recorded.

Required conceptual fields:

| Field | Type | Requirements |
|---|---|---|
| `Id` | 64-bit integer or UUID | Stable primary key. |
| `ParentId` | nullable node identifier | Null only for the root. References another `JobNode`. |
| `Description` | text | Required and non-blank. |
| `WriteUp` | nullable text | Optional detailed description. |
| `PostedByUserId` | user identifier | Required. |
| `OwnerUserId` | nullable user identifier | Optional except on the permanent root, which must always have a non-null owner. `NULL` means unassigned — the node is in the public pickup pool (ownership model §2.1, ADR 0031). Ownership is not evidence of work. |
| `ExpectedDuration` | nullable duration | Non-negative estimate. |
| `ExpectedCost` | nullable decimal amount | Non-negative estimate; not the dynamically calculated actual cost. |
| `NeededStart` | nullable instant | Optional planning constraint. |
| `NeededFinish` | nullable instant | Must be later than `NeededStart` when both are present. |
| `Priority` | priority identifier | Required. |
| `Kind` | node-kind identifier | Required. |
| `PostedAt` | instant | Required. |
| `ArchivedAt` | nullable instant | Set when removed from normal operational views; never implies physical deletion. |
| `Version` | concurrency token | Required for optimistic concurrency. |

### 4.2 Hierarchy invariants

The following rules shall hold after every committed transaction:

1. There is exactly one root node.
2. The root has no parent.
3. Every non-root node has exactly one parent.
4. A node cannot be its own parent.
5. Parent relationships are acyclic.
6. Every node is reachable from the root.
7. The root cannot have `LeafWork`.
8. A branch has one or more children and no `LeafWork`.
9. A leaf has no children and zero or one `LeafWork`.
10. A node can never have both a child and `LeafWork`.

A leaf with no `LeafWork` is valid. It represents planned or placeholder work whose execution state has not yet been created. A `LeafWork` with no sessions is also valid and allows criteria and assignment state to be prepared before work starts; it is not evidence that work occurred.

Operations which convert a leaf into a branch, move nodes, decompose work, or attach `LeafWork` shall execute atomically so intermediate invalid states are not externally visible.

### 4.3 LeafWork

A `LeafWork` stores the execution and achievement state of one leaf. It does not contain worked time and is not, by itself, evidence that work occurred.

Required conceptual fields:

| Field | Type | Requirements |
|---|---|---|
| `JobNodeId` | node identifier | Primary key and foreign key to a leaf `JobNode`; establishes the zero-or-one relationship. |
| `Achievement` | achievement status | Required. |
| `PartialCriteria` | nullable text | Optional. |
| `FullCriteria` | nullable text | Optional. |
| `ChangedAt` | instant | Required. |
| `Version` | concurrency token | Required. |

Only a leaf may own `LeafWork`. `LeafWork` cannot remain attached to a node which becomes a branch.

### 4.4 WorkSession

A `WorkSession` is the sole representation of a continuous interval of actual work. A `LeafWork` may have zero or more sessions, including sessions by different users.

Required conceptual fields:

| Field | Type | Requirements |
|---|---|---|
| `Id` | 64-bit integer or UUID | Stable primary key. |
| `LeafWorkId` | leaf-work identifier | Required foreign key. |
| `WorkedByUserId` | user identifier | Required. |
| `StartedAt` | instant | Required. |
| `FinishedAt` | nullable instant | Null while active; otherwise later than `StartedAt`. |
| `ChangedAt` | instant | Required. |
| `Version` | concurrency token | Required. |

Starting work creates a session. Pausing or stopping work finishes that session. Resuming creates a new session for the same `LeafWork`. These lifecycle operations do not modify the job tree.

`Pause`, `stop`, and `finish session` are user-interface descriptions of the same domain operation: setting `FinishedAt` on the active session. They do not imply different persisted session states. A later resume always creates a new session.

At most one session for the same user and `LeafWork` may be active at once. Sessions for the same user and `LeafWork` shall not overlap. Sessions for the same user on different leaves may overlap intentionally and participate in concurrent allocation.

Workers may correct their own historical sessions, including start and finish instants. Job managers and administrators may correct any session. Every historical correction requires a reason and an audit record containing the previous and replacement values. Corrections shall revalidate interval ordering, same-leaf overlap, schedule-independent session rules, authorization, and optimistic concurrency. No second-person approval is required.

An unfinished session is costed to the current instant supplied by the application or reporting operation. Business logic shall use an injected clock rather than reading system time throughout the calculation.

### 4.5 Decomposing a leaf after work has begun

If a leaf with existing `LeafWork` is genuinely decomposed into child jobs, the operation shall be explicit and atomic:

1. create a child representing the work already performed, with a meaningful user-supplied description;
2. move the existing `LeafWork` and all its sessions to that child;
3. create the newly identified child jobs; and
4. convert the original node into their branch parent.

The operation shall preserve session identifiers, users, times, audit history, and applicable historical rate semantics. It shall not be used merely to pause or resume work.

### 4.6 Archival and deletion

Completed jobs shall never be physically deleted and shall remain in the database indefinitely because hierarchy and session history are inputs to dynamic costing. Completion may make a job read-only for ordinary structural operations, but authorized historical session corrections remain possible and audited.

Completed jobs may be archived to remove them from default operational views. Archival preserves identifiers, parentage, ownership, prerequisites, leaf work, sessions, rates, derived achievement, and audit history. Archived ancestors remain traversable for reporting and authorization.

Any job with `LeafWork`, a `WorkSession`, a completed descendant, cost-relevant history, or an audit-retention dependency shall not be physically deleted. Physical deletion, if supported at all, is limited to unused planning nodes after an impact check proves that no historical, prerequisite, ownership, or costing reference depends upon them.

## 5. Achievement

### 5.1 Canonical statuses

The modern model shall use named achievement values rather than unexplained single-character codes. At minimum, it shall distinguish:

- waiting;
- in progress;
- success; and
- explicitly defined non-success terminal outcomes required by the new product, such as cancelled or unsuccessful.

Legacy single-character codes are not part of the new model and shall not appear in database or public contracts.

Only canonical `Success` satisfies a prerequisite. Partial, failed, cancelled, waiting, and in-progress states do not satisfy one.

### 5.2 Derived branch achievement

Achievement is evaluated recursively:

- A leaf with `LeafWork` succeeds only when that `LeafWork` has canonical status `Success`.
- A leaf without `LeafWork` is not successful.
- A branch succeeds if and only if every direct child succeeds.
- Therefore, a branch succeeds if and only if every leaf in its subtree succeeds.
- The root follows the same recursive achievement rule, although it cannot contain `LeafWork`.

Derived branch achievement should normally be calculated from authoritative child state. If cached for query performance, the cache is disposable and shall be updated transactionally or be safely rebuildable.

## 6. Prerequisites

A prerequisite edge is directed from the required job to the job that depends upon it:

`RequiredJob -> DependentJob`

The dependent job's prerequisite is satisfied only when the required job has derived status `Success`. If the required job is a branch, every descendant leaf must therefore succeed.

Prerequisites attached to a branch apply to the branch and every descendant. Consequently, a leaf is ready only when every prerequisite attached directly to that leaf or to any of its ancestors is satisfied. This makes a prerequisite on a branch an effective gate for work in that subtree. Readiness diagnostics shall identify the prerequisite edge and the ancestor at which an inherited prerequisite was declared.

The following rules shall be enforced:

1. Both endpoints reference existing `JobNode` rows.
2. A node cannot require itself.
3. Duplicate edges are prohibited.
4. The prerequisite graph is acyclic.
5. An edge is prohibited when either endpoint is an ancestor or descendant of the other in the job hierarchy.

The final rule prevents redundant or contradictory dependencies inside a subtree. Moving a node must revalidate all prerequisite edges affected by the move.

Deleting an otherwise eligible unused planning job requires an explicit domain operation that either removes its prerequisite edges with confirmed impact or rejects the deletion. Silent cascading that changes workflow semantics is prohibited. Completed and cost-relevant jobs follow the retention rules in section 4.6 and cannot be deleted.

An unsatisfied prerequisite is a hard command gate:

- a new `WorkSession` cannot be started for the dependent job;
- the dependent `LeafWork` cannot transition to `Success` or another completed state;
- the start and completion commands shall recheck prerequisites inside their write transaction; and
- an existing session may still be paused or finished so prerequisite regression cannot trap an active session; finishing a session is distinct from completing its `LeafWork`.

Prerequisites govern whether work may start and whether `LeafWork` may enter a completed state. They do not determine whether recorded labour incurred cost. Once a `WorkSession` has been validly created, its elapsed eligible time remains costable and participates in concurrent allocation even if a prerequisite later becomes unsatisfied. This prevents current workflow state from erasing historical labour or changing the allocation of concurrent sessions. Any session created through corruption or an unauthorized direct database write is still evidence of recorded labour; costing includes it, while integrity diagnostics and audit handling expose the violation rather than silently rewriting cost history.

The UI shall explain every unsatisfied prerequisite before offering execution actions. Database enforcement and application authorization shall prevent a direct request from bypassing the gate.

## 7. Users and time zones

Each user shall have:

- a stable identifier;
- login and display names;
- an IANA time-zone identifier;
- a default hourly rate, when applicable;
- visibility and authorisation properties; and
- optimistic concurrency metadata.

### 7.1 Employee accounts and authentication

JobTrack is a single-organisation, employee-only system. It shall use locally managed ASP.NET Core Identity accounts stored in the application database. There is no external identity provider and no public self-registration.

Accounts shall be created, enabled, disabled, and assigned permissions by an administrator. Disabling an employee account shall prevent new authentication and revoke existing sessions without deleting the employee's work, audit history, schedule, or rate records.

Authentication shall use secure server-side application behaviour and protected authentication cookies:

- password hashes shall be produced and verified only through the supported ASP.NET Core Identity password hasher;
- plaintext passwords and reusable reset secrets shall never be stored or logged;
- authentication cookies shall be `Secure`, `HttpOnly`, use an appropriate `SameSite` policy, have bounded lifetimes, and be renewed or revoked deliberately;
- login attempts shall be rate-limited and repeated failures shall cause a bounded account lockout;
- authentication, logout, failed login, lockout, password change, reset, account disablement, and permission changes shall be audited without recording secrets;
- state-changing requests shall use antiforgery protection;
- session revocation shall use the account security stamp or an equivalent server-validated credential version; and
- generic login failure messages shall not reveal whether an employee account exists.

MFA is not required in the initial release. The account and authentication design shall nevertheless reserve a clean extension point for passkeys. The initial release may include disabled passkey capability detection or schema-compatible credential storage, but shall not expose a non-functional enrolment flow or weaken password authentication in anticipation of it.

A client/end-user requester (§7.3's `Requester` role) is a role grant on this same administrator-provisioned employee-account model, not a separate identity boundary, authentication scheme, or public self-registration path (ADR 0033). Every rule in this section applies identically to a Requester account.

### 7.2 Account provisioning and password reset

There is no automated end-user account recovery. An employee who loses access contacts an administrator.

The normal reset flow shall:

1. require an authenticated administrator;
2. issue a short-lived, single-use reset grant or temporary credential;
3. store only a cryptographic hash of any reset secret;
4. require the employee to choose a new password at next sign-in;
5. revoke existing sessions and outstanding reset grants; and
6. audit the administrator, target account, reason, and result without recording the secret.

A database-backed emergency mechanism shall remain available when the normal web administration flow cannot be used. It shall be exposed through a narrowly scoped administration command-line tool or equivalent controlled operational procedure which uses the application's configured password hasher and a transaction. It may set a generated temporary credential or create a one-time reset grant, mark the account as requiring a password change, increment the security stamp, and revoke existing sessions.

Administrators shall not manually write password hashes, place plaintext passwords in SQL scripts, or use a general-purpose database function callable by the application login role. Emergency reset access shall use a separate least-privileged operational database role and shall be audited.

The first administrator shall be created by an explicit one-time bootstrap command using an interactive secret or protected secret input. Production startup shall not create a default account or default password.

### 7.3 Authorization

Authorization shall be policy-based and enforced in the application layer for every command and sensitive query. Hiding a control in Razor is not authorization.

The baseline roles are:

| Role | Baseline authority |
|---|---|
| Administrator | Manage employee accounts, account state, global role assignments, system configuration, and all job data. |
| Job manager | Manage the complete job hierarchy, prerequisites, leaf work, and work sessions, but not employee credentials or global security configuration. |
| Worker | View employees and job data; manage jobs they own and their owned subtrees; on a node they control (own or inherit from an ancestor owner), record work sessions for any worker, not only their own — a Worker who controls nothing on a subtree may record no session there, own or otherwise, until they pick up an unassigned node or are given one they own (ownership model §4.2/§4.3, ADR 0031/0032); manage their own schedule and exceptions; cannot edit another employee's account, schedule, or rate. |
| Rate manager | Additive permission to manage employee rates and node rate overrides without granting employee-account administration or cost visibility. |
| Cost viewer | Additive permission to view rates, rate provenance, and calculated costs. |
| Auditor/read-only | Read job, work, schedule, and audit information without mutation; rate and cost access remains separately controlled. |
| Requester | Create a request into a configured holding area (ownership model addendum, ADR 0033); view only a requester-safe progress projection of their own submitted requests (and department requests where explicitly configured); add requester-visible notes/clarifications; request cancellation subject to staff confirmation. Cannot pick up, own, decompose, move, reassign, or edit any job node; cannot record work sessions; cannot view rates, costs, schedules, employee lists, audit detail, or any job outside their own permitted requests. |

Roles may be combined. `Cost viewer` is deliberately separate because visibility of employees does not imply visibility of their rates or costs.

A job's direct owner is optional, except on the permanent root: `NULL` means unassigned, placing the node in the public pickup pool that any Worker, job manager, or administrator may claim (ownership model §2.1/§4.3, ADR 0031). New children default to whatever owner the creator supplies, including unassigned. An actor who controls a node — directly owns it or inherits control from an owned ancestor — may reassign it to any user or release it to the pool; this is not restricted to job managers or administrators, since a controlling worker already holds full structural authority over their subtree (ownership model §4.4, ADR 0031). A worker may mutate only nodes they control; structural operations affecting a node outside their control require job-manager or administrator authority, or control of an ancestor. Ownership shall never be inferred from the posting user, current worker, or client-supplied claims.

All authenticated employees holding any role other than `Requester` may view other employees' non-secret profile, schedule, job, and work information. A `Requester` account sees only its own requester-safe progress projection (§13.6, ADR 0033) — "any authenticated employee" is not sufficient for full job browsing once a `Requester` role exists, and existing read queries must not treat it as such. Workers may edit their own schedules and exceptions, but not another employee's. Only administrators may edit employee accounts or global role assignments. Rate editing requires its management policy; ordinary visibility never grants mutation.

Authorization decisions shall default to deny and shall account for disabled accounts, current role assignments, subtree scope, target user, operation, and sensitivity of the requested fields. Query handlers must prevent sensitive fields from being loaded or serialised for unauthorized callers.

No routine operation requires approval by a second person. Security shall rely on explicit authority, strong audit records, least privilege, and prompt revocation rather than dual-control workflows.

### 7.4 Time representation

Events such as work starts, work finishes, rate boundaries, exception boundaries, and audit events represent instants. They shall be persisted unambiguously:

- PostgreSQL: `timestamptz`, treated as an instant and normally written/read in UTC;
- SQLite: a canonical UTC representation, preferably an integer count of microseconds or milliseconds since the Unix epoch, with conversion owned by the application.

Recurring schedules are civil-time rules and cannot be represented by an offset alone. Each schedule is interpreted using the user's IANA time zone, including daylight-saving transitions.

The application shall define deterministic handling for invalid or ambiguous local times:

- a local time skipped by a daylight-saving transition shall resolve according to a documented policy; and
- an ambiguous repeated local time shall resolve to an explicitly selected earlier or later occurrence.

The selected policy shall be covered by automated tests.

Changing a user's current time zone shall not reinterpret existing schedule versions. Each historical schedule version retains its recorded IANA zone. A new or changed current schedule is represented by a new effective-dated schedule version using the newly selected zone.

Every cost result and diagnostic trace shall record the TZDB version used for civil-time expansion. Reproducibility requires persisted state, `asOf`, and that TZDB version. A deployment may deliberately upgrade its bundled TZDB data; recalculation under the new version may then change historical results and shall be disclosed in release notes and cost diagnostics. If exact reproduction under an older TZDB version is operationally required, that version must remain available to the calculation service rather than being inferred from current host data.

## 8. Working schedules

### 8.1 Effective-dated schedules

Working schedules shall be historical and effective-dated. Editing a current schedule creates a new schedule version rather than rewriting the version used for earlier dates.

A schedule version contains:

| Field | Requirements |
|---|---|
| User | Required. |
| Effective start date | Required, interpreted in the user's time zone. |
| Effective end date | Optional, exclusive. |
| Time-zone identifier | Required snapshot of the zone used by this schedule version. |
| Weekly intervals | Zero or more civil-time intervals associated with days of the week. |

Effective ranges for schedule versions belonging to the same user shall not overlap.

### 8.2 Weekly intervals

A day may contain multiple working intervals. An interval may cross midnight. Cross-midnight intervals should be normalised into deterministic local-date segments during calculation or storage.

Overlapping or adjacent weekly intervals within one schedule version should be normalised to their union. No instant may be counted twice merely because schedule rows overlap.

### 8.3 Schedule exceptions

Schedule exceptions are user-specific instant ranges with one of two effects:

- `AddWorkingTime`, used for overtime or other exceptional work and optionally carrying an explicit hourly `RateOverride`; or
- `RemoveWorkingTime`, used for leave, holidays, or other unavailable periods.

The effective working set for a user is:

`(scheduled intervals union additive exceptions) minus subtractive exceptions`

Subtractive exceptions take precedence where additive and subtractive exceptions overlap. All resulting intervals are normalised before costing. An additive exception allows work outside ordinary scheduled hours to generate cost. Where an effective additive exception has an explicit `RateOverride`, that rate applies only inside the exception interval and takes precedence over node overrides, effective-dated user rates, and the user's default rate. An additive exception without a rate uses ordinary rate precedence.

For one user, two explicitly priced additive exceptions shall not overlap. This avoids ambiguous competing overtime prices; adjacent priced exceptions are valid. Unpriced additive exceptions may overlap and are normalised to their union. A subtractive exception suppresses both eligibility and any overtime rate throughout its overlap.

Exceptions must have a non-empty range. The system should record a reason and the user or administrator who created each exception.

## 9. Labour rates

Rates are hourly monetary rates and shall use a fixed-precision decimal representation. Binary floating-point shall never be used for money.

JobTrack uses one installation-wide currency: Pounds Sterling, represented by ISO 4217 code `GBP`. Currency is not selectable per user, job, rate, or report. Persist amounts at sufficient fixed precision for allocation; round presented or exported monetary totals to pounds and pence using decimal midpoint-to-even rounding unless a later accounting requirement explicitly replaces that policy.

### 9.1 User rates

Each user may have effective-dated `UserCostRate` rows. For a given user, their effective ranges must not overlap. Adjacent ranges are valid. Gaps are permitted only when the user's default rate can supply the rate; otherwise costing shall report an explicit missing-rate error.

Overlap prevention must be enforced transactionally:

- PostgreSQL should use a range column or generated range expression with an exclusion constraint scoped by user.
- SQLite requires triggers for insert and update, plus application validation within the same write transaction.

### 9.2 Job-specific rate overrides

A node may define effective-dated rate overrides for a particular user. An override applies to that node and all descendants during its effective range unless a closer descendant defines an override for the same user at the costed instant.

Overrides for the same node and user shall not have overlapping effective ranges. Adjacent ranges are valid. PostgreSQL shall enforce this with a range exclusion constraint; SQLite shall enforce it transactionally with triggers and library validation.

For a `WorkSession` at instant `t`, search from its `LeafWork` node toward the root and select the first override for the session's worker whose effective range contains `t`. If a closer node has no override effective at `t`, continue toward the root. This is the effective nearest-ancestor rule.

### 9.3 Rate precedence

At each costed instant, the applicable hourly rate is selected in this order:

1. an explicit rate on an effective `AddWorkingTime` exception covering that instant;
2. nearest node or ancestor override effective for the worker at that instant;
3. the user's effective-dated `UserCostRate`; then
4. the user's default rate.

Absence of all four sources is a costing error. It shall not silently produce zero cost.

Rate changes are interpreted as interval boundaries. Cost calculations must split work intervals at every applicable rate boundary.

## 10. Dynamic costing

Costs are calculated dynamically. Stored rate, schedule, hierarchy, work, or exception changes may therefore change historical results. The UI and API shall not present calculated costs as immutable accounting entries.

### 10.1 Eligible time

For a user, cost is generated only at instants which are within that user's effective working set. Merely leaving a `WorkSession` active overnight does not generate overnight cost unless an additive exception covers that time.

An active work session at instant `t` satisfies:

`StartedAt <= t AND (FinishedAt IS NULL OR t < FinishedAt)`

For reporting, a null `FinishedAt` is bounded by the single calculation time (`asOf`) captured at the start of the operation.

### 10.2 Concurrent allocation

At an eligible instant, find all active `WorkSessions` for the user. If there are `N` sessions, each receives an equal `1/N` share of that user's cost at that instant. `N` has no business-defined upper limit: two-session overlap is only the simplest case, and 20 or more simultaneous sessions must be handled correctly.

A recorded session remains eligible for costing regardless of the job's current prerequisite state. A prerequisite-blocked session contributes cost and is a member of `N`; cost diagnostics may report the current blocked state, but shall not use it to change allocation.

The division is by active work session. Sessions belonging to different leaves count separately irrespective of whether those leaves share a parent or top-level job.

For active work session `s` at instant `t`:

`costRate(s, t) = applicableHourlyRate(s, t) / activeWorkSessionCount(user(s), t)`

Because job overrides may differ between concurrent work sessions, each session's applicable rate is resolved independently before its own equal time share is applied. Equal sharing applies to time, not necessarily to the final currency amount.

#### 10.2.1 Interval overlap

Let two finite, valid half-open session intervals be:

- `I1 = [start1, end1)` where `end1 > start1`; and
- `I2 = [start2, end2)` where `end2 > start2`.

They do not overlap when:

`start2 >= end1 OR start1 >= end2`

Equivalently:

`end1 <= start2 OR end2 <= start1`

Otherwise they overlap. Their intersection is:

`overlapStart = max(start1, start2)`

`overlapEnd = min(end1, end2)`

`overlapDuration = overlapEnd - overlapStart`

The half-open convention means sessions which merely touch at a boundary, such as one ending exactly when another starts, have zero overlap.

For exactly two eligible concurrent sessions belonging to the same user, each receives one-half of the eligible overlap duration. Outside the overlap, the sole active session receives the full eligible duration. Within a segment where rates are constant:

`session1OverlapCost = overlapHours / 2 * applicableRate(session1)`

`session2OverlapCost = overlapHours / 2 * applicableRate(session2)`

For example, if session 1 is `[09:00, 12:00)` and session 2 is `[11:00, 13:00)`, allocation is:

- `[09:00, 11:00)`: session 1 receives the full two hours;
- `[11:00, 12:00)`: each session receives one-half hour from the one-hour overlap; and
- `[12:00, 13:00)`: session 2 receives the full hour.

Before applying these formulas, intervals are clipped to the report range and `asOf`, and sessions are intersected with the user's effective working intervals. Rate boundaries inside an overlap split it into smaller constant-rate segments. Current prerequisite state does not remove a recorded session.

Pairwise overlap detection is sufficient to explain two sessions but not to calculate arbitrary concurrency. With three, 20, or any number of sessions, the implementation shall use the boundary-partition algorithm in section 10.3. Each maximal segment is allocated by its actual active-session count `N`, preventing pairwise double counting. For example, a segment with 20 eligible active sessions allocates `1/20` of its duration to each session.

The implementation shall not encode a fixed concurrency limit or use an algorithm whose correctness depends on enumerating pairs. Resource-protection limits may bound report ranges, result sizes, or execution time, but shall not change the allocation result for a valid calculation.

Overlap between sessions for the same user on different leaves is valid and drives cost allocation. Overlap between sessions for the same user and the same `LeafWork` is invalid under section 4.4 and shall be rejected rather than allocated.

#### 10.2.2 Database-wide concurrency discovery

Costing a requested job or subtree shall not consider only sessions inside that subtree. For every worker represented by a requested session, the calculation must find all of that worker's sessions anywhere in the database which overlap the relevant calculation interval. Sessions on unrelated jobs affect `N` and therefore the requested job's allocated time and cost.

For a finite query interval `[queryStart, queryEnd)`, a candidate session overlaps when:

`WorkedByUserId = userId`

`AND StartedAt < queryEnd`

`AND (FinishedAt IS NULL OR FinishedAt > queryStart)`

The strict inequalities preserve half-open boundary semantics. An unfinished session uses `asOf` as its effective end after candidate retrieval. The implementation may query a wider safe candidate set and filter precisely in the domain engine, but it shall never omit a potentially overlapping session.

### 10.3 Calculation algorithm

For a requested reporting range and set of jobs:

1. Capture one `asOf` instant.
2. Expand each requested job to its descendant `LeafWork` rows and requested `WorkSessions`.
3. Determine the relevant workers and calculation intervals from those requested sessions.
4. Query all potentially overlapping sessions for those workers across the entire database, including sessions outside the requested jobs.
5. Clip every relevant work interval to the reporting range and `asOf`.
6. Build each user's effective working intervals from the correct historical schedule versions and exceptions.
7. Intersect work intervals with effective working intervals.
8. Add boundaries for every work start/end, schedule start/end, exception start/end, user-rate change, and any other event that can change the result.
9. Partition time into maximal segments over which active session membership, working eligibility, and rates are constant.
10. Count every active session in each segment and divide its duration by that count `N`, regardless of how large `N` is; current prerequisite state does not alter membership. The allocation is represented exactly as the rational quantity `segmentDuration / N` until monetary multiplication and aggregation. It shall not be converted into whole ticks by largest-remainder or other residual assignment because that would make nominally equal sessions receive unequal time.
11. Resolve each session's applicable rate using the precedence rules.
12. Multiply allocated hours by rate, retain only requested-job amounts for the requested result, and aggregate through the requested hierarchy.

Calculations shall retain exact duration shares and sufficient decimal monetary precision, and round currency only at an explicitly documented reporting boundary. Final displayed or exported totals shall be rounded to the `GBP` minor unit using midpoint-to-even rounding. Intermediate segment and allocation values shall not be rounded to ticks or pennies. The product shall define before implementation whether displayed parent totals must reconcile to displayed child totals and, if so, the deterministic penny-reconciliation policy.

### 10.4 Hierarchical totals

The actual cost of a leaf is the sum of its `WorkSession` costs; a leaf without sessions costs zero. The actual cost of a branch is the sum of all descendant leaf costs. The root cost is the sum of all work in the system for the requested interval.

`ExpectedCost` remains a planning value and is not included in actual cost.

## 11. Recommended logical schema

The target schema should contain, at minimum:

- `job_node`;
- `leaf_work`;
- `work_session`;
- `job_prerequisite`;
- `app_user`;
- ASP.NET Core Identity credential/account tables, separated from `app_user` employee-domain profile data;
- `user_schedule_version`;
- `user_schedule_interval`;
- `user_schedule_exception`;
- `user_cost_rate`;
- `node_rate_override`;
- `achievement_status` or an equivalent constrained enum;
- `priority`; and
- `audit_event`.

Cached closure or achievement tables may be added for performance, but they are derived data rather than authoritative state.
Root/Branch/Leaf node labels are likewise derived from `job_node.parent_id` and child existence at read time, not stored in a `node_kind` table (ADR 0035).

Authentication credentials and employee-domain data shall not share a catch-all table. The Identity account has a one-to-one relationship with the employee profile while password hashes, security stamps, reset state, and future passkey credentials remain isolated from ordinary employee queries. Normalized usernames shall have a case-insensitive unique constraint consistent with login normalization.

All foreign-key columns and common temporal lookup columns shall be indexed. At minimum, indexes are needed for:

- `job_node(parent_id)`;
- `job_node(owner_user_id, archived_at)` for owner-scoped operational views;
- both directions of `job_prerequisite`;
- composite indexes beginning with `work_session.worked_by_user_id` and then `started_at` and `finished_at`, supporting database-wide overlap discovery without scanning other users' sessions;
- schedule versions by user and effective range;
- exceptions by user and range;
- user rates by user and range; and
- node overrides by node, user, and effective range.

For PostgreSQL, the reference design should use a GiST index over `(worked_by_user_id, tstzrange(started_at, finished_at, '[)'))`, using an unbounded upper range for unfinished sessions, so the overlap operator can answer user-scoped interval queries directly. Retain or add measured B-tree indexes such as `(worked_by_user_id, started_at)` and `(worked_by_user_id, finished_at)` when query plans demonstrate value, including a partial index for unfinished sessions if needed.

For SQLite, create at least `(worked_by_user_id, started_at)` and `(worked_by_user_id, finished_at)` indexes, with a measured partial index for unfinished sessions where supported and useful. SQLite may use one index to narrow candidates and apply the other temporal predicate as a filter; integration tests shall inspect real query plans and dataset-scale performance rather than assuming both B-tree indexes will be combined.

Standalone timestamp indexes are insufficient for this query pattern because concurrency is scoped by worker. User identity shall be the leading key in B-tree overlap-search indexes.

## 12. Database-first implementation and enforcement

### 12.1 Database source of truth

The project is database-first. The reviewed PostgreSQL and SQLite schemas, versioned SQL migrations, constraints, queries, transaction workflows, and deterministic test datasets shall exist and pass database tests before library feature implementation begins.

Database-first means the relational design and observable database contracts are established before the library; it does not mean placing all business orchestration in stored procedures. PostgreSQL remains authoritative where it can enforce an invariant directly. SQLite shall have an explicitly designed trigger or transaction-scoped enforcement path for the same invariant. The library later supplies authorization, workflow orchestration, and consumer-focused APIs without redefining the schema; the front end follows only after the library contract is validated.

The three delivery gates are:

1. **Database:** both providers have migrations, invariant tests, canonical queries, deterministic fixtures, and documented concurrency behaviour. PostgreSQL additionally meets production performance and operational requirements.
2. **Library:** the provider-neutral public contract, domain behaviour, commands, queries, and provider conformance suite are complete without any ASP.NET dependency.
3. **Front end:** ASP.NET Core composes the library and adds authentication, HTTP/UI policy enforcement, and presentation without direct database access or duplicated domain behaviour.

Work shall not proceed through a gate while a required behaviour is known to lack a credible implementation in either database.

Schema artefacts shall be maintained as source-controlled SQL:

- an ordered baseline and forward-only versioned migrations;
- extensions and database roles;
- tables, types, constraints, indexes, functions, and triggers;
- reference data separate from scenario test data;
- deterministic small test scenarios;
- scalable generated performance datasets;
- canonical read/query definitions where a stable database contract is useful; and
- verification queries for every invariant and migration.

Application startup shall not use automatic schema creation or silently mutate production schema. Schema deployment is an explicit, separately authorized operation. Every migration shall run against an empty database and a database at each supported upgrade starting point.

### 12.2 Database test API and datasets

A dedicated test-support library shall provide a typed API for database tests to execute the supported queries, inserts, updates, and structural operations. It shall include:

- disposable real-PostgreSQL and file-backed SQLite database lifecycle management;
- migration application and schema-version assertions;
- deterministic identity and clock control;
- scenario builders for users, trees, leaf work, sessions, prerequisites, schedules, exceptions, and rates;
- command helpers for valid mutations;
- deliberately unsafe/raw helpers confined to integrity tests which prove the database rejects invalid states;
- typed query results for hierarchy, eligibility, overlap candidates, rate resolution, and costing inputs;
- transaction, isolation, concurrency, and advisory-lock helpers;
- database-error assertions based on stable constraint identifiers rather than full message text;
- query-plan capture and index-use assertions; and
- fixture cleanup without weakening production constraints.

The test API is test infrastructure, not the public application API. It shall not bypass authentication or authorization tests at the web/application boundary, and its raw mutation capability shall never be referenced by production projects.

Test data shall cover minimal examples, boundary cases, invalid cases, schema-upgrade cases, realistic end-to-end scenarios, and generated scale cases. Stable “golden” scenarios shall include exact expected hierarchy, achievement, overlap, rate provenance, and `GBP` costing results. Generated data shall be reproducible from a recorded seed and shall include 20+, 100+, deep-tree, broad-tree, long-history, and many-user concurrency cases.

### 12.3 General enforcement strategy

Simple row-local invariants shall use `NOT NULL`, `UNIQUE`, foreign-key, and `CHECK` constraints. Cross-row and cross-table invariants require transactional enforcement.

The library shall expose controlled domain commands for structural changes. Front ends shall never write tables directly. Validation in library code improves error messages but does not replace database enforcement where concurrent writes can violate an invariant; SQLite shall additionally use transaction-scoped library enforcement where an equivalent database constraint is unavailable.

### 12.4 PostgreSQL

PostgreSQL is the primary datastore and reference implementation for every deployment topology. It provides stronger native support for:

- exclusion constraints for non-overlapping temporal ranges;
- recursive common-table expressions;
- deferrable constraints and constraint triggers;
- range types and range indexes; and
- transactional advisory locks where tree-wide serialisation is needed.

Hierarchy, leaf/branch exclusivity, prerequisite acyclicity, and ancestor-edge checks should use deferred constraint triggers so valid multi-step structural operations can complete within one transaction before final validation.

Use `numeric(19,6)` rather than PostgreSQL `money` for hourly rates, overrides, and precise monetary inputs. Final report values are rounded to `GBP` pennies according to section 9; calculated totals remain derived and are not authoritative stored currency rows.

Use a generated or consistently constructed `tstzrange` for work-session ranges, with an unbounded upper end for unfinished sessions. Use GiST exclusion constraints to reject overlap for the same user and `LeafWork`, while allowing overlap across different leaves. Add a partial unique index on `(leaf_work_id, worked_by_user_id)` for rows whose `finished_at` is null.

Use exclusion constraints for non-overlapping user-rate, node-rate-override, and schedule-version effective ranges. Use `ON DELETE RESTRICT` for cost-relevant and historical relationships. Completed or cost-relevant jobs are archived, never cascaded away.

Cost reports shall capture one `asOf` and execute in a consistent repeatable-read snapshot so hierarchy, sessions, schedules, prerequisites, and rates cannot come from mutually inconsistent committed states.

Use explicit application-managed `bigint` concurrency versions on mutable domain rows rather than exposing PostgreSQL transaction internals as the public concurrency contract.

Enforce exactly one root with a deferred database invariant, retain it permanently after bootstrap, and prohibit its deletion. “At most one root” is insufficient.

Audit rows shall be append-only to application roles. Prefer structured columns for actor, operation, entity, correlation, and timestamp, with `jsonb` before/after payloads only where their flexibility is justified. Index audit access paths deliberately and consider time partitioning only after measured volume warrants it.

### 12.5 SQLite

SQLite is a supported secondary platform for embedded or single-node deployments with modest concurrent write demand. Its schema and enforcement strategy shall be designed and tested in the database phase alongside PostgreSQL, not retrofitted after the library or front end. It shall expose the same observable library contract, but PostgreSQL remains the reference for production performance and multi-writer operation. SQLite requires more application-owned enforcement:

- enable foreign keys on every connection;
- use triggers for temporal-overlap and cross-table guards;
- serialise structural mutations using immediate write transactions;
- use recursive CTEs to validate hierarchy and prerequisites; and
- define one canonical timestamp encoding because SQLite has no native instant type.

SQLite triggers cannot provide PostgreSQL-style deferred constraint triggers. Structural commands may therefore need carefully ordered writes or an application transaction that uses temporary staging followed by a validated replacement operation.

PostgreSQL-specific optimizations are acceptable only when they preserve a straightforward SQLite execution path and do not leak into the common schema contract or public API. Provider differences shall remain internal to the persistence layer. The shared conformance suite shall verify identical domain results, stable error categories, transaction rollback behaviour, and cost outputs on both engines. Engine-specific operational limits, such as SQLite's single-writer behaviour, shall be documented and may differ, but successful operations shall have equivalent domain effects.

### 12.6 Invariant enforcement matrix

| Invariant | PostgreSQL reference enforcement | SQLite enforcement |
|---|---|---|
| Exactly one root | Partial unique index plus a deferred invariant which also forbids zero roots | Triggers and serialized structural command |
| Tree acyclicity and reachability | Deferred constraint trigger using a recursive CTE | Recursive-CTE triggers and immediate write transaction |
| Leaf/branch exclusivity | Deferred constraint triggers on `job_node` and `leaf_work` | Triggers and ordered transactional writes |
| Prerequisite DAG and hierarchy-edge exclusion | Deferred constraint trigger using recursive CTEs | Recursive-CTE trigger and application validation |
| No self-parent or self-prerequisite | `CHECK` constraint | `CHECK` constraint |
| Session ordering | `CHECK` constraint | `CHECK` constraint |
| No same-user, same-leaf session overlap | GiST exclusion constraint over an unbounded `tstzrange`, plus a partial unique index for unfinished sessions | Trigger plus immediate write transaction |
| Non-overlapping schedule and rate effective ranges | Range exclusion constraints scoped by their owning keys | Triggers plus application validation |
| Achievement | Computed from authoritative leaf state | Computed from authoritative leaf state |

Deferred validation is required where a valid move or decomposition temporarily passes through an otherwise invalid intermediate state. Preflight checks provide useful errors, but the database constraint remains authoritative under concurrent writes.

## 13. Library and front-end architecture

### 13.1 Architectural boundary

The reusable .NET 10 library is the product core. It shall own domain rules, application commands and queries, authorization of domain operations, persistence, transaction boundaries, audit generation, and dynamic costing. Front ends express user intent through the library and shall not access JobTrack tables, reproduce domain rules, or implement an alternative cost algorithm.

PostgreSQL is the primary provider and SQLite is a supported secondary provider behind the same public contract. Both persistence designs begin in the database phase. Provider-specific SQL, migrations, constraint handling, and error translation shall be isolated inside the persistence implementation rather than exposed to consumers.

Recommended project boundaries are:

- **Abstractions**: stable public command, query, result, identifier, error, and client contracts; no database-provider dependency;
- **Domain**: pure entities, value objects, interval algebra, achievement, rate resolution, and costing; no I/O;
- **Application**: command/query orchestration, authorization, transactions, and audit intent;
- **PostgreSQL persistence**: the reference Npgsql implementation, SQL, migrations, and database-error translation;
- **SQLite persistence**: the conforming secondary implementation, SQLite migrations, and compensating transactional enforcement;
- **Database migrator**: explicit, provider-specific deployment of versioned schema and reference data;
- **ASP.NET Core**: the primary front end, containing HTTP/UI concerns, Identity authentication, policy entry checks, and problem-details mapping;
- **Other front ends**: CLI, worker, batch, or future desktop clients consuming the same public library; and
- **Tests**: domain unit tests, public-contract tests, provider conformance tests, PostgreSQL-specific integrity/performance tests, and front-end end-to-end tests.

These may be separate assemblies or equivalently enforced modules. Dependency direction shall keep Domain and Abstractions independent of ASP.NET Core, Npgsql, SQLite, and presentation concerns.

A recommended initial solution layout is:

| Project | Responsibility |
|---|---|
| `JobTrack.Abstractions` | Stable public facade, immutable request/result contracts, identifiers, enums, and public error types; no provider dependency. |
| `JobTrack.Domain` | Pure interval algebra, achievement, rate resolution, and cost allocation; no I/O. |
| `JobTrack.Application` | Commands, queries, authorization, orchestration, transaction intent, and audit intent. |
| `JobTrack.Persistence.PostgreSql` | Required Npgsql provider, SQL, mappings, transactions, migrations, and constraint-error translation. |
| `JobTrack.Persistence.Sqlite` | Conforming provider based on the SQLite schema and transaction design established during the database phase. |
| `JobTrack.Database` | Explicit forward-only schema deployment and reference-data tooling. |
| `JobTrack.Web` | Primary ASP.NET Core browser UI and HTTP API, Identity authentication, endpoint policy checks, and problem-details mapping. |
| `JobTrack.TestSupport` | PostgreSQL lifecycle, deterministic scenarios, builders, integrity-test helpers, and query-plan capture. |

The precise assembly count may change, but these dependency boundaries shall not be collapsed. In particular, the web project shall not become the reusable application layer, and the PostgreSQL implementation shall not leak Npgsql types into public contracts.

### 13.2 Public library contract

The library shall expose a cohesive asynchronous facade, divided into discoverable command and query capabilities rather than exposing repositories or a public unit of work. Its contract shall:

- use immutable request and result types and strongly typed identifiers where they prevent accidental interchange;
- use task-based asynchronous methods with cancellation;
- return empty collections rather than null collections;
- avoid database entities, provider exceptions, SQL types, and mutable persistence objects in public results;
- include optimistic-concurrency versions in mutation requests and results;
- provide detailed cost results with allocated intervals, applicable rates, rate provenance, and current prerequisite-state diagnostics; and
- maintain one provider-neutral public contract; any released secondary provider shall conform to it.

Consumers shall require one configured entry point, such as `IJobTrackClient`, which groups cohesive job, work-session, schedule, rate, query, audit, and costing capabilities. Registration shall provide parallel composition methods, such as `AddJobTrackPostgreSql` and `AddJobTrackSqlite`, without exposing connections or repositories to front ends or changing consumer-facing commands and results.

The public surface shall follow the .NET Framework Design Guidelines summarized in `Framework_Design_Guidelines_Essentials.md`. In particular:

- asynchronous methods use the task-based asynchronous pattern, an `Async` suffix, and a final optional `CancellationToken`;
- request and result contracts are immutable and collections are exposed as read-only abstractions, never mutable concrete collections;
- expected absence uses an explicit nullable or `Try`-style contract, while operational failures use a shallow, documented exception hierarchy;
- public methods use strongly typed identifiers and value objects where they prevent primitive interchange, but avoid speculative abstractions;
- properties are reserved for cheap, stable values; I/O and calculations are methods; and
- public naming, parameter order, exception behaviour, and nullability form a compatibility commitment.

Representative capabilities include jobs, work sessions, prerequisites, schedules, rates, achievement queries, readiness queries, auditing, and costing. The facade may group these into cohesive sub-services, but consumers should require only one configured JobTrack entry point.

Every command receives an authenticated actor and correlation context. The library shall authorize domain scope and ownership using authoritative stored data; it shall not trust caller-supplied role claims as the sole authorization decision. ASP.NET Core additionally enforces endpoint policies. Trusted service or administrative contexts must be explicit, least-privileged, and auditable rather than implicit bypasses.

Errors shall be translated into a shallow, stable set of library exceptions or result categories which callers can handle distinctly, including not found, authorization denied, concurrency conflict, prerequisite blocked, missing rate, and invariant violation. Persistence code shall translate PostgreSQL SQLSTATE plus named constraints, and the SQLite equivalents, without matching free-form database message text. Provider exceptions shall not cross the public boundary.

### 13.3 Transactions and persistence

One logical mutation shall use one connection and one transaction. An internal unit-of-work mechanism may compose persistence operations, but shall not be public. Multi-step operations such as moving a subtree or decomposing a worked leaf remain atomic and rely on provider-appropriate invariant enforcement.

PostgreSQL access should use a single configured, pooled `NpgsqlDataSource`. SQLite shall use a configured connection factory and an immediate write transaction where serialization is required. Mutations shall update application-managed versions with a compare-and-swap predicate; a zero-row update caused by a stale version is a concurrency conflict.

Cost reports shall capture one `asOf` value and read from a consistent snapshot. PostgreSQL shall use `REPEATABLE READ`. The SQLite provider shall use a read transaction that gives an equivalent stable snapshot for the duration of the calculation. Structural operations may use PostgreSQL advisory locks where measured concurrency risks require serialization; the SQLite provider shall use its write-transaction semantics.

SQL shall be parameterized and owned by the provider implementation. The choice of low-level mapper or data-access library is an implementation decision and shall not alter the public contract. Application startup shall never auto-create or silently migrate a production database.

### 13.4 Pure cost engine

Cost allocation shall be implemented as a deterministic, side-effect-free domain function over immutable, fully materialized inputs plus the captured `asOf`. Persistence implementations discover all required sessions, schedules, exceptions, prerequisites, and rates within the consistent snapshot; the pure engine performs the boundary-partition algorithm in section 10.3.

A PostgreSQL set-based implementation may later optimize very large reports. It is an optimization, not a second definition of behaviour, and shall pass differential conformance tests against the pure engine on golden and generated datasets. SQLite may use a different retrieval strategy but shall feed equivalent inputs to the same engine.

### 13.5 Commands

Use immutable value objects for instants, intervals, money, rates, and identifiers where practical. All structural commands shall use optimistic concurrency and a database transaction. Examples include:

- create or move a node, archive a completed node, or delete a proven-unused planning node;
- attach or remove leaf work;
- start, pause, resume, finish, or correct a work session;
- decompose a worked leaf into child nodes while preserving its leaf work and sessions;
- add or remove prerequisite;
- change schedule version;
- add schedule exception; and
- change rates.

### 13.6 Front-end integration

The modern ASP.NET Core application is the primary interface and shall include both the main browser experience and an HTTP API. It shall use ASP.NET Core Identity for authentication, construct the actor context, call the library facade, and map stable library errors to RFC 7807 problem details. API contracts shall use descriptive status names and ISO 8601 timestamps with offsets. Database-specific identifiers, provider errors, and legacy one-character codes shall not leak into HTTP contracts.

HTTP endpoints shall map user intent to library commands and queries rather than exposing CRUD over persistence entities. The browser UI is a client of the same application boundary and shall not receive privileged repository or database access. Endpoint policies provide coarse-grained admission; the library rechecks ownership, subtree scope, versions, and current domain state using authoritative data inside the operation.

Other front ends may reference the same library directly. They shall receive identical domain behaviour and authorization enforcement and shall not be forced to call through the ASP.NET application. Provider selection and connection configuration occur at composition time through dependency injection or an equivalent host mechanism; ordinary consumers remain provider-agnostic.

Async cancellation, bounded database command timeouts, structured logging, and tracing shall flow through the library boundary. Logs and telemetry shall include operation, actor identifier, correlation identifier, duration, and affected identifiers where appropriate, but never credentials, authentication secrets, unrestricted PII, or rate data.

A `Requester` actor's progress view is a distinct, narrow query surface (a requester-safe projection rooted at their own request node, ADR 0033) — it shall be implemented as its own query, never by relaxing `/Jobs/Browse` or another operational query's authorization to admit `Requester`.

## 14. Greenfield scope and legacy exclusions

This is a greenfield system. No users, jobs, work records, hierarchy, credentials, configuration, or other data shall be imported from the legacy SQL Server system. The legacy implementation is evidence about past workflows only; it is not a compatibility contract.

The following legacy mechanisms are explicitly excluded unless a future product requirement specifies new behaviour independently of the old schema:

- node substitution and `SubstituteNodeId`;
- `ContinuesSibling` sequencing;
- `AllowsLeaves`;
- authoritative `TreeStore` closure data;
- authoritative `NodeAchs` or achievement snapshots;
- stored UI view state;
- legacy ASP.NET database users and schemas;
- legacy single-character status codes; and
- legacy pause/resume through hierarchy restructuring.

Derived closure or achievement caches may be introduced only as disposable performance optimizations under the rules in sections 5 and 11. They are not continuations of the legacy tables.

## 15. Initialisation and schema evolution

Each new installation starts with an empty operational dataset. Explicit schema deployment shall create:

1. the required database extensions, roles, schemas, tables, constraints, indexes, functions, and triggers;
2. stable reference data such as achievement statuses, node kinds, priorities, and authorization roles;
3. an uninitialised installation state in which the exactly-one-root invariant is not yet armed;
4. the first administrator through a one-time, audited bootstrap command using normal password hashing; and
5. exactly one permanent structural root, posted and owned by that administrator, before atomically marking the installation initialised.

The bootstrap command shall create the administrator, root, and initialised marker in one database transaction after the credential secret has been validated and hashed. Once initialised, the database shall reject deletion or re-parenting of the root, creation of another root, and transition back to the uninitialised state. No ordinary committed operational state may contain zero roots.

Development, demonstration, and test scenario data shall be separate from production reference data and shall never be inserted by production startup. Forward-only schema migrations remain required for upgrades between JobTrack versions; they evolve JobTrack-owned schema and data and are unrelated to importing the legacy system.

## 16. Security auditing and data protection

Changes to job structure, work intervals, achievement, prerequisites, schedules, exceptions, and rates shall be audited. Each audit event should include:

- actor;
- timestamp;
- operation;
- affected entity and identifier;
- correlation identifier; and
- before/after values or an equivalent structured change representation.

Viewing or changing costs and rates shall require explicit permissions. Schedule exceptions and historical edits should record a reason.

Audit records shall be append-only to normal application roles. Administrative actions shall not be able to erase their own audit evidence through the application. Retention, archival, privileged database access, backup protection, and audit-integrity monitoring shall be defined before production deployment.

Password hashes, reset-grant hashes, security stamps, and passkey credential material shall be treated as authentication secrets. They shall not appear in ordinary employee queries, exports, logs, exception details, audit before/after payloads, or application telemetry.

Threat modelling shall cover at least credential stuffing, session theft, cross-site request forgery, cross-site scripting, authorization bypass, subtree-scope confusion, insecure direct object references, mass assignment, malicious file or text content if attachments are introduced, sensitive logging, database credential compromise, and abuse of emergency password reset.

## 17. Testing requirements

Development shall follow test-driven development. Tests shall be written before implementing each behaviour.

### 17.1 Domain unit tests

At minimum, cover:

- leaf, branch, and root classification;
- leaf work with zero, one, and many sessions;
- pause and resume without hierarchy mutation;
- multiple users contributing sessions to one leaf;
- rejection of overlapping sessions for the same user and leaf;
- atomic decomposition of a worked leaf with session preservation;
- recursive branch success;
- unsuccessful and empty leaves;
- inherited, effective-dated nearest-ancestor node-rate resolution;
- effective-dated node overrides, boundary changes, gaps, and overlap rejection;
- half-open interval boundaries;
- concurrent equal-time allocation;
- differing overrides on concurrent work;
- unfinished work with a fixed `asOf` instant;
- schedules crossing midnight;
- multiple daily intervals;
- effective-dated historical schedules;
- additive overtime exceptions;
- subtractive leave exceptions;
- overlapping additive and subtractive exceptions;
- daylight-saving gaps and repeated local times;
- dynamic historical recalculation;
- continued costing and concurrency participation when a prerequisite becomes unsatisfied after a session starts;
- and decimal rounding policy.

Property-based tests are recommended for interval normalisation and allocation conservation. For any user and costed interval, allocated time across work sessions must equal eligible worked time and must never exceed it.

### 17.2 Database integration tests

Run the complete behavioural contract against both PostgreSQL and SQLite from the database phase onward. PostgreSQL integration tests shall additionally cover its constraints, deferred triggers, transaction isolation, advisory-lock behaviour, and representative query plans at production scale. SQLite shall pass the common behavioural suite and provider-specific tests for trigger enforcement, immediate-write transactions, stable read snapshots, and documented concurrency limits. Verify rejection of:

- a second root;
- parent cycles;
- orphaned nodes;
- `LeafWork` on the root;
- `LeafWork` on a branch;
- adding a child to a node with `LeafWork` outside the explicit decomposition operation;
- overlapping sessions for the same user and `LeafWork`;
- missing job ownership;
- starting a session or marking `LeafWork` complete while prerequisites are blocked;
- physical deletion of completed or cost-relevant jobs;
- duplicate or cyclic prerequisites;
- ancestor/descendant prerequisites;
- overlapping user-rate ranges; and
- concurrently submitted writes which would jointly violate an invariant.

### 17.3 Authentication and authorization tests

At minimum, verify:

- employee-only provisioning and absence of public registration;
- successful and failed login without account enumeration;
- lockout and rate limiting;
- password hashing and password-change invalidation;
- normal and emergency administrator reset flows;
- forced password change and single-use reset expiry;
- revocation of existing sessions after reset, disablement, or security-sensitive changes;
- antiforgery rejection for state-changing browser requests;
- default-deny behaviour;
- each role and permitted role combination;
- worker mutation inside and rejection outside assigned subtrees;
- workers changing their own sessions but not another employee's sessions;
- workers changing their own schedules and exceptions but not another employee's;
- workers changing owned jobs but not jobs owned by another employee;
- employee visibility without employee-edit authority;
- independent rate/cost visibility;
- query projection which omits unauthorized sensitive fields; and
- immutable, secret-free security audit events.

### 17.4 End-to-end tests

Cover creation and restructuring of jobs, starting and finishing work, prerequisite gating, achievement propagation, schedule/rate administration, overtime, and cost reporting.

All automated test commands shall be run with `gtimeout`.

## 18. Acceptance criteria

The modernised system is acceptable when:

1. all hierarchy and prerequisite invariants are enforced under concurrent writes;
2. only `WorkSessions` contribute actual cost;
3. overnight in-progress work produces no cost outside effective working intervals;
4. explicit overtime exceptions enable cost outside normal schedules;
5. concurrent work receives equal `1/N` time shares for any active-session count, without a fixed concurrency limit;
6. inherited rate overrides and rate precedence produce deterministic results;
7. branch achievement and prerequisite satisfaction are recursively correct;
8. all calculations are timezone- and daylight-saving-safe;
9. historical schedule and rate changes dynamically affect calculated costs;
10. a new installation starts without importing legacy operational or identity data;
11. the shared behavioural suite passes against PostgreSQL and SQLite, with provider-specific integrity, concurrency, migration, and query-plan tests also passing;
12. cost calculations are reproducible given the same persisted state and `asOf` instant;
13. no unauthenticated request can access employee or job data;
14. every sensitive operation is protected by an explicit authorization policy at the application boundary;
15. worker job-ownership and own-session restrictions hold under direct HTTP requests, not only through the UI;
16. administrators can securely provision, disable, revoke, and reset local employee accounts without exposing reusable secrets;
17. every job has exactly one owner and worker mutation is restricted to that ownership;
18. prerequisite-blocked work cannot be started or marked complete, while an existing recorded session remains stoppable, costable, and included in concurrent allocation if prerequisites later become unsatisfied;
19. completed and cost-relevant jobs are retained indefinitely and can only be archived; and
20. all monetary values and reports use `GBP` with the defined rounding policy.
