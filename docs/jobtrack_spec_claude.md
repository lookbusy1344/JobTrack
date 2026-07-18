# JobTrack — Rebuild Specification

**Status:** Draft 3 (aligned)
**Date:** 2026-07-04
**Authority:** This is the **secondary** specification. `jobtrack_spec_codex.md` is the **primary, authoritative** specification; this document supplies implementation detail only where consistent with it, and any conflict is resolved in favour of the primary specification.
**Supersedes:** legacy SQL Server database `JobTrack` (`jobtrack_legacy.sql`)
**Lineage:** merges the stronger elements of `jobtrack_spec_codex.md` into the
original `jobtrack_spec_claude.md`, resolving the three conflicts recorded in §15.

---

## 1. Purpose & Scope

JobTrack records hierarchical jobs, their prerequisites, actual work performed,
recursively-derived achievement, working schedules, labour rates, and
dynamically-calculated cost.

The rebuild targets:

- **Primary datastore:** PostgreSQL (latest stable, 16+). Postgres is the
  reference implementation; every invariant, function, and cost query is designed
  against Postgres first and its behaviour is authoritative.
- **Supported secondary datastore:** SQLite (latest stable) for single-node / embedded use. It
  must reproduce the same observable behaviour, with more enforcement pushed into
  the library because SQLite lacks exclusion constraints and deferred triggers.
- **The product is a library.** A **provider-primarily-Postgres data-access
  library** (C#, `net10.0`) owns all persistence and the cost engine and is the
  single reusable core. Front ends are thin: a **modern ASP.NET Core (.NET 10)**
  web/API application is the primary front end, and the same library is directly
  consumable by **other front ends** — a CLI, background/worker services, batch
  reporting jobs, and future clients — without going through the web app.
- **Approach:** *database-first*. The schema, invariants, and a deterministic
  seed/test dataset exist and pass database tests before application features are
  built. The library performs all inserts/updates/selects and the cost algorithm;
  no front end touches the database directly.
- **Build order (strict):** **(1) database design**, then **(2) library**, then
  **(3) front end**. Each layer is specified and validated before the next begins;
  the database is authoritative, the library is the reusable core, the front ends
  are thin clients. This is a specification only — no implementation is undertaken
  here.
- **Greenfield:** no data migration is required. The system starts empty and is
  populated from reference + seed data (§14); the legacy database informs the
  design but is not carried over.
- **Public API conventions:** the library's public surface follows the .NET
  **Framework Design Guidelines** (Cwalina & Abrams, 4th ed.), summarised for this
  project in `Framework_Design_Guidelines_Essentials.md`. Conformance points are
  consolidated in §12.9 and applied throughout §12 and Appendix B.

The system shall not treat a `JobNode` or a `LeafWork` row as evidence that work
occurred. **Only a `WorkSession` represents an interval of actual work.**

---

## 2. Terminology & Conventions

| Term | Meaning |
|---|---|
| Job hierarchy | The single rooted tree of `JobNode` parent relationships. |
| Root | The sole `JobNode` with no parent. Structural; cannot hold `LeafWork`. |
| Branch | A node with ≥1 children and no `LeafWork`. |
| Leaf | A node with no children; may hold zero or one `LeafWork`. |
| LeafWork | Execution, criteria, and achievement state of one leaf. Not worked time. |
| WorkSession | One continuous interval one user worked (or is working) for one LeafWork. |
| Prerequisite | Directed "must succeed before eligible" edge between jobs. |
| Success | The only achievement outcome that satisfies a prerequisite. |
| Working interval | Time a user is considered available, for cost calculation. |
| Schedule exception | A dated interval that adds to or removes from working time. |
| Active session | A `WorkSession` whose interval contains the instant being costed. |
| `asOf` | The single calculation instant captured at the start of a cost operation. |

**All time intervals are half-open `[start, end)`** — includes start, excludes
end. Sessions touching at a boundary (one ends exactly as another starts) do not
overlap.

Business logic uses an **injected clock**; it never reads system time mid-calculation.

---

## 3. Domain Model

### 3.1 JobNode

| Field | Type | Requirements |
|---|---|---|
| `id` | bigint identity | Stable PK. |
| `parent_id` | nullable node id | Null **only** for the root. |
| `description` | text | Required, non-blank. |
| `writeup` | nullable text | Optional detail. |
| `posted_by_user_id` | user id | Required (who created it). |
| `owner_user_id` | nullable user id | Optional except on the permanent root (always required there). `NULL` = unassigned, the public pickup pool (ownership model, ADR 0031); not evidence of work. |
| `priority` | priority id | Required (fk lookup). |
| `kind` | node-kind id | Required (fk lookup). |
| `expected_duration_hours` | nullable numeric | Non-negative estimate. |
| `expected_cost` | nullable numeric | Non-negative estimate; not actual cost. |
| `needed_start` | nullable instant | Planning constraint. |
| `needed_finish` | nullable instant | `> needed_start` when both present. |
| `posted_at` | instant | Required. |
| `archived_at` | nullable instant | Set on archival; never implies deletion. |
| `version` | bigint | Optimistic-concurrency token, required. |

`substitute_node_id`, `allows_leaves`, and `continues_sibling` from the legacy
model are **not** carried into the core design; see §15 legacy decisions.

### 3.2 Hierarchy invariants (hold after every committed transaction)

1. Exactly one root; the root has no parent.
2. Every non-root node has exactly one parent.
3. A node cannot be its own parent.
4. Parent relationships are acyclic.
5. Every node is reachable from the root.
6. The root cannot hold `LeafWork`.
7. A branch has ≥1 children and no `LeafWork`.
8. A leaf has no children and zero-or-one `LeafWork`.
9. A node can never have both a child and `LeafWork`.

A leaf with no `LeafWork` is a valid **planning placeholder**. A `LeafWork` with
no sessions is valid (criteria/assignment prepared before work starts) and is not
evidence of work. Operations that convert leaf↔branch, move nodes, decompose
work, or attach `LeafWork` execute **atomically**; intermediate invalid states are
never externally visible.

Schema deployment leaves the installation explicitly uninitialised. One bootstrap
transaction creates the first administrator, creates the permanent root posted and
owned by that administrator, and arms the exactly-one-root invariant. Once armed,
the database rejects a second root, deletion or re-parenting of the root, and any
transition back to the uninitialised state.

### 3.3 LeafWork (zero-or-one per leaf)

| Field | Type | Requirements |
|---|---|---|
| `job_node_id` | node id | PK and FK to a **leaf** node (enforces 0..1). |
| `achievement` | ach status | Current derived-or-recorded state. |
| `partial_criteria` | nullable text | |
| `full_criteria` | nullable text | |
| `changed_at` | instant | Required. |
| `version` | bigint | Concurrency token. |

Only a leaf may own `LeafWork`; it cannot remain attached to a node that becomes
a branch.

### 3.4 WorkSession (the only record of actual work)

| Field | Type | Requirements |
|---|---|---|
| `id` | bigint identity | PK. |
| `leaf_work_id` | leaf-work id | Required FK. |
| `worked_by_user_id` | user id | Required. |
| `started_at` | instant | Required. |
| `finished_at` | nullable instant | Null while active; else `> started_at`. |
| `changed_at` | instant | Required. |
| `version` | bigint | Concurrency token. |

Start creates a session; pause/stop finishes it; resume creates a new session for
the same `LeafWork` — **without modifying the tree**. A `LeafWork` may have many
sessions, including by different users. **Sessions for the same (user, LeafWork)
must not overlap**; sessions for the same user on *different* leaves may overlap
and drive concurrent allocation (§10).

Unfinished sessions are costed to `asOf`. Workers may correct their own historical
sessions; managers/admins any session. Every correction requires a reason and an
audit record with before/after values, and revalidates ordering, same-leaf
overlap, authorization, and optimistic concurrency. No second-person approval.

### 3.5 Decomposing a worked leaf

If a leaf that already has `LeafWork` is genuinely decomposed, one atomic op:
(1) create a child for the work already done; (2) move the existing `LeafWork`
and all its sessions to that child; (3) create the newly identified child jobs;
(4) convert the original node into their branch parent. Session identifiers,
users, times, audit history, and rate semantics are preserved. This is never used
for mere pause/resume.

### 3.6 Retention, archival, deletion

Cost-relevant history is an input to dynamic costing, so it is **retained
indefinitely**. Completed jobs are made read-only for ordinary structural ops but
remain open to audited historical session corrections, and may be **archived**
(removed from default views, all relationships preserved and traversable).

Physical deletion is prohibited for any node with `LeafWork`, a `WorkSession`, a
completed descendant, cost-relevant history, or an audit dependency. Deletion, if
supported at all, is limited to unused planning nodes after an impact check. FK
actions use **`ON DELETE RESTRICT`** for cost-relevant and historical
relationships — never cascade.

---

## 4. Achievement

### 4.1 Statuses

Named statuses, not bare codes: `Waiting`, `InProgress`, `Success`, and the
non-success terminal outcomes selected for the new product. No ordered achievement
threshold participates in success.

Only `Success` satisfies a prerequisite.

### 4.2 Derived achievement (recursive, computed not stored)

- A leaf with `LeafWork` succeeds iff that `LeafWork` has canonical `Success`.
  `Waiting`, `InProgress`, and non-success terminal states never succeed.
- A leaf without `LeafWork` is not successful.
- A branch succeeds iff **every** direct child succeeds — hence iff every leaf in
  its subtree succeeds. The root follows the same rule.

Achievement is derived from authoritative state via recursive CTE views/functions
(the legacy `NodeAchs` cache and its `InvalidAchLookup` reconciliation view are
removed). Any performance cache is disposable, transactionally maintained, and
safely rebuildable — never authoritative.

---

## 5. Prerequisites

Edge is directed `required_job → dependent_job`; satisfied only when
`required_job` has derived status `Success` (so requiring a branch requires every
descendant leaf — the "prerequisite tree needs all children" rule for free).

Enforced rules:
1. Both endpoints reference existing nodes.
2. A node cannot require itself.
3. Duplicate edges prohibited.
4. The prerequisite graph is acyclic.
5. An edge is prohibited when either endpoint is an ancestor or descendant of the
   other in the hierarchy (prevents redundant/contradictory intra-subtree deps).

Moving a node revalidates affected edges. Prerequisite is a **hard execution
gate**: a new `WorkSession` cannot be *started*, and the dependent `LeafWork`
cannot *transition to a completed state*, while any prerequisite is unsatisfied;
start/complete commands recheck inside their write transaction. Eligibility is
dynamic — when prerequisites later reach `Success`, the gate opens; if a
prerequisite later ceases to succeed, the gate closes again.

> **Costing note (decision §15.3):** the gate restricts *starting/completing*
> work. It does **not** exclude an already-open session from cost. An open session
> on a prerequisite-blocked job **incurs cost and counts toward `N`** like any
> other open session. (This deliberately differs from the Codex spec.)

The UI explains every unsatisfied prerequisite before offering execution actions;
database enforcement + application authorization prevent bypass.

---

## 6. Users, Authentication & Authorization

### 6.1 Users

Each user has: stable id; login + display names; **IANA time-zone id**; optional
default rate; visibility/authorization properties; and an optimistic-concurrency
token. JobTrack is a **single-organisation, employee-only** system: locally
managed **ASP.NET Core Identity** accounts in the application database, no
external IdP, no public self-registration.

Credential/account data (password hashes, security stamps, reset state, future
passkey material) live in Identity tables **separated** from the `app_user`
employee-domain profile (1:1). Normalized usernames have a case-insensitive
unique constraint.

### 6.2 Provisioning & reset

Admins create/enable/disable/permission accounts. Disabling blocks new
authentication and revokes sessions without deleting work/audit/schedule/rate
history. Reset: admin-initiated, short-lived single-use grant, hash-only storage,
forced change at next sign-in, session + outstanding-grant revocation, audited
without secrets. A separately-least-privileged **emergency CLI** (using the
configured password hasher in a transaction) exists for when the web flow cannot
be used. No plaintext passwords or hashes are hand-written into SQL. The first
admin is created by an explicit one-time bootstrap; production startup creates no
default account.

Authentication baseline: Identity password hasher only; `Secure`/`HttpOnly`/
`SameSite` cookies with bounded lifetime; login rate-limiting + bounded lockout;
antiforgery on state-changing requests; security-stamp-based revocation; generic
(non-enumerating) failure messages; auth events audited without secrets. MFA is
out of scope for v1 but the design reserves a clean passkey extension point
without exposing a non-functional flow.

### 6.3 Authorization & ownership

Policy-based, enforced in the application layer for **every** command and
sensitive query (hiding a Razor control is not authorization). Default-deny.

| Role | Baseline authority |
|---|---|
| Administrator | Accounts, roles, config, all job data. |
| Job manager | Full hierarchy, prerequisites, leaf work, sessions; not credentials/security config. |
| Worker | View all job/employee data; manage jobs they control (own or inherit from an ancestor owner) + owned subtrees; on a controlled node, record sessions for any worker, not just their own; manage own schedule/exceptions. |
| Rate manager | Additive: manage employee rates and node overrides without account administration or cost visibility. |
| Cost viewer | Additive: view rates, rate provenance, calculated costs. |
| Auditor / read-only | Read without mutation; rate/cost access separately controlled. |

Roles combine; `Cost viewer` is deliberately separate (seeing employees ≠ seeing
their rates). **A job's direct owner is optional** (`NULL` = unassigned pool,
except the permanent root); children default to whatever owner the creator
supplies, including unassigned. Any actor who controls a node (owns it or
inherits control from an owned ancestor) may reassign or release it — not
restricted to manager/admin (ownership model §4.4, ADR 0031). Ownership is
never inferred from posting user, current worker, or client claims. Workers
mutate only nodes they control, and record sessions only on nodes they
control. Query handlers
must prevent sensitive fields being loaded/serialised for unauthorized callers.
No routine operation requires dual control.

---

## 7. Time & Time Zones

Instants (work start/finish, rate/exception boundaries, audit) are persisted
unambiguously:

- **PostgreSQL:** `timestamptz`, treated as an instant, written/read in UTC.
- **SQLite:** a canonical UTC encoding — integer microseconds since the Unix
  epoch — with conversion owned by the application.

Recurring schedules are **civil-time** rules interpreted through the user's IANA
zone, including DST transitions; a stored offset alone is insufficient. Working
hours are materialized to UTC intervals through the zone for the specific date.

**DST edge policy (must be tested):** a local time skipped by a spring-forward
transition resolves per a documented rule (default: shift forward to the first
valid instant); an ambiguous repeated local time (autumn) resolves to an
explicitly selected occurrence (default: the earlier). Both are covered by
automated tests.

---

## 8. Working Schedules

### 8.1 Effective-dated schedule versions

Schedules are historical: editing a current schedule creates a new **version**
rather than rewriting the version used for earlier dates.

| Field | Requirements |
|---|---|
| `user_id` | Required. |
| `effective_start` | Required, interpreted in the user's zone. |
| `effective_end` | Optional, exclusive. |
| `timezone` | Snapshot of the zone used by this version. |
| weekly intervals | Zero or more civil-time intervals per weekday. |

Effective ranges for one user's schedule versions **must not overlap**.

### 8.2 Weekly intervals

A weekday may have multiple intervals; an interval may cross midnight
(normalised into deterministic local-date segments). Overlapping/adjacent
intervals within a version are normalised to their union — no instant counted
twice.

### 8.3 Schedule exceptions (incl. overtime)

User-specific instant ranges, two effects:

- `AddWorkingTime` — overtime / exceptional work; **may carry its own explicit
  `rate_override`** (the distinct, per-exception overtime rate).
- `RemoveWorkingTime` — leave/holiday/unavailable.

Effective working set = `(scheduled ∪ additive exceptions) − subtractive
exceptions`; subtractive wins on overlap; all normalised before costing. An
additive exception is what allows out-of-hours work to generate cost. Exceptions
have a non-empty range and record a reason + creator.

For one user, explicitly priced additive exceptions must not overlap; adjacent
priced exceptions are valid. Unpriced additive exceptions may overlap and are
normalised to their union. A subtractive exception suppresses both eligibility
and any overtime rate throughout its overlap.

---

## 9. Labour Rates

Hourly monetary rates, fixed-precision decimal (`numeric(19,6)` in Postgres — not
`money`; never binary float). **Currency is installation-wide GBP** (ISO 4217),
not selectable per anything. Amounts held at full precision for allocation;
presented/exported totals rounded to pounds/pence with **midpoint-to-even
(banker's) rounding**.

### 9.1 User cost rates

Each user may have effective-dated `user_cost_rate` rows; their effective ranges
**must not overlap** (adjacent OK). Gaps are permitted only when the user's
default rate can supply the rate; otherwise costing raises an explicit
**missing-rate error** — never silent zero.

### 9.2 Node rate overrides (inherited, effective-dated)

A node may define effective-dated overrides for a particular user. An override
applies to that node **and all descendants** during its effective range, unless a
closer descendant defines an override for the same user at the costed instant
(**effective nearest-ancestor rule**: from the session's leaf node toward the
root, take the first override for the worker whose range contains `t`). Overrides
for the same (node, user) must not overlap in time (adjacent OK).

### 9.3 Rate precedence

At each costed instant `t`, for session `s` on node `n` by user `u`, the hourly
rate is selected:

1. an `AddWorkingTime` exception covering `t` that carries an explicit
   `rate_override` (the overtime rate). **The overtime rate supersedes node
   overrides** — an explicitly-costed block of overtime is the most deliberate
   statement of what the time is worth, so it wins over any node/ancestor override.
2. else the nearest node/ancestor override effective for `u` at `t` (§9.2);
3. else `u`'s effective-dated `user_cost_rate`;
4. else `u`'s default rate.

Absence of all is a **costing error** (`MissingRateException`, §12.6), never a
silent zero. Rate changes are interval boundaries; work intervals split at every
applicable boundary. `resolve_rate` (Appendix C) and the in-process cost engine
apply this exact order.

---

## 10. Dynamic Costing

Costs are **calculated dynamically** from current authoritative state; stored
rate/schedule/hierarchy/work/exception changes may change historical results, so
the UI/API must not present calculated costs as immutable accounting entries.

### 10.1 Eligible time

Cost is generated only at instants inside the user's **effective working set**
(§8.3). Leaving a session active overnight generates no cost unless an additive
exception covers that time. An active session at `t` satisfies
`started_at <= t AND (finished_at IS NULL OR t < finished_at)`; a null
`finished_at` is bounded by `asOf`.

### 10.2 Concurrent allocation

At an eligible instant, take **all** the user's active sessions. If there are `N`,
each receives an equal `1/N` share of that user's time at that instant. `N` has no
upper bound (2 is the simplest case; 20+ must work). Division is by **active work
session**; sessions on different leaves count separately regardless of shared
ancestry.

Because concurrent sessions may resolve different rates (differing node overrides,
or one under an overtime exception), each session's rate is resolved
independently, then its equal *time* share applied. Equal sharing is of **time**,
not necessarily of currency.

For session `s` at eligible instant `t`:
`costRate(s,t) = applicableHourlyRate(s,t) / activeSessionCount(user(s), t)`.

> **Blocked-but-open sessions (decision §15.3):** an open session on a
> prerequisite-blocked job **is** an active session — it incurs cost and is counted
> in `N`. The gate only prevents *starting/completing* such work. Cost diagnostics
> still flag the block for visibility.

#### 10.2.1 Pairwise overlap (the two-session case)

`I1=[s1,e1)`, `I2=[s2,e2)` (`e>s`) do **not** overlap iff `s2>=e1 OR s1>=e2`;
otherwise `overlap=[max(s1,s2), min(e1,e2))`. Within the overlap (and within
eligible, constant-rate time) `N=2`, each gets half; outside, the sole active
session gets the full duration. Example `[09:00,12:00)` & `[11:00,13:00)`:
`[09,11)`→s1 full; `[11,12)`→½ each; `[12,13)`→s2 full.

#### 10.2.2 Database-wide concurrency discovery

Costing a job/subtree must **not** consider only sessions inside that subtree. For
every worker with a requested session, find *all* their sessions **anywhere in the
database** overlapping the calculation interval, because unrelated sessions change
`N`. Candidate test for `[queryStart, queryEnd)`:
`worked_by_user_id = u AND started_at < queryEnd AND (finished_at IS NULL OR
finished_at > queryStart)`; unfinished sessions use `asOf` as their end. Never
omit a potentially overlapping session.

### 10.3 Algorithm (boundary-partition / sweep line)

For a requested reporting range and set of jobs:

1. Capture one `asOf`.
2. Expand each requested job to descendant `LeafWork` and requested sessions.
3. Determine relevant workers + calculation intervals.
4. Query **all** overlapping sessions for those workers across the whole database.
5. Clip every interval to the reporting range and `asOf`.
6. Build each user's effective working intervals from the correct historical
   schedule versions + exceptions.
7. Intersect work intervals with effective working intervals.
8. Add boundaries for every session start/end, schedule start/end, exception
   start/end, and rate change.
9. Partition into maximal segments over which active-session membership,
   eligibility, and rates are constant.
10. In each segment, count active sessions `N` and give each the exact rational
    share `duration/N` (regardless of how large `N` is). Do not assign indivisible
    residual ticks to selected sessions.
11. Resolve each session's rate by precedence (§9.3); multiply allocated hours ×
    rate.
12. Retain requested-job amounts and aggregate through the hierarchy.

The whole calculation runs against a single `asOf` in a **repeatable-read
snapshot** so hierarchy/sessions/schedules/prerequisites/rates cannot come from
mutually inconsistent committed states. Exact duration shares and internal monetary
precision are retained; currency is rounded only at the documented final boundary
(GBP minor unit, banker's rounding). The product must define hierarchy-display
penny reconciliation before implementation. No fixed concurrency limit and no
pair-enumeration algorithm.

### 10.4 Hierarchical totals

Leaf cost = Σ its session costs (a leaf with no sessions costs zero). Branch cost
= Σ descendant leaf costs. Root cost = all work in the requested interval.
`expected_cost` is a planning value, excluded from actual cost.

---

## 11. Logical Schema & Enforcement

Minimum tables (`snake_case`; surrogate keys `GENERATED ALWAYS AS IDENTITY` /
`INTEGER PRIMARY KEY`; mutable rows carry `version`, `created_at`, `updated_at`):

`job_node`, `leaf_work`, `work_session`, `job_prerequisite`, `app_user`, ASP.NET
Core Identity tables (separated from `app_user`), `user_schedule_version`,
`user_schedule_interval`, `user_schedule_exception`, `user_cost_rate`,
`node_rate_override`, `achievement_status`, `priority`,
`audit_event`. Optional derived caches (closure / achievement) are non-authoritative.

### 11.1 Enforcement matrix

| Invariant | PostgreSQL | SQLite fallback |
|---|---|---|
| Single root (exactly one) | partial unique index on `parent_id IS NULL` + **deferred** invariant forbidding zero roots | triggers |
| Tree acyclic / reachable | **deferred constraint trigger**, recursive-CTE reachability | recursive-CTE trigger |
| Leaf⊕branch exclusivity | deferred constraint triggers on `job_node`/`leaf_work` | triggers |
| Prereq DAG + no ancestor/descendant edge | deferred constraint trigger, recursive CTE | recursive-CTE trigger |
| No self parent/prereq | `CHECK` | `CHECK` |
| Session ordering; `finished_at > started_at` | `CHECK` | `CHECK` |
| No same-(user,LeafWork) session overlap | GiST `EXCLUDE` on `(worked_by_user_id WITH =, tstzrange(started_at, finished_at,'[)') WITH &&)` (unbounded upper for open) + partial unique on open sessions per (leaf, user) | triggers + immediate write txn |
| No overlap: user_cost_rate / node override / schedule version effective ranges | range `EXCLUDE` scoped by user (and node) | triggers + app validation |
| Achievement (I §4.2) | computed | computed |

**Deferred** constraint triggers let a valid multi-step structural operation
(move, decompose) complete inside one transaction before final validation.
Engine divergence is expected and documented; the same behavioural test suite runs
against both engines.

Precision: `numeric(19,6)` for rates/overrides (not `money`). Structural mutations
use optimistic `version` tokens and a transaction. Cost reports run in
repeatable-read.

---

## 12. Library Architecture (primary deliverable)

The library is the product. It is Postgres-primary, provider-abstracted, and
consumable by any .NET front end through one dependency-injection entry point.

### 12.1 Solution layout

| Project | Target | Responsibility | Depends on |
|---|---|---|---|
| `JobTrack.Abstractions` | `net10.0` | Public contracts only: command/query `record`s, result DTOs, domain enums, the `IJobTrackClient` facade, error types. No SQL, no Npgsql. | — |
| `JobTrack.Domain` | `net10.0` | Pure domain: interval algebra, achievement rules, rate precedence, and the **cost engine** as a pure function. No I/O. | Abstractions |
| `JobTrack.Data` | `net10.0` | Persistence: **EF Core 10** (`Npgsql.EntityFrameworkCore.PostgreSQL` primary + `Microsoft.EntityFrameworkCore.Sqlite` supported secondary provider) for ordinary mapping, command change tracking, LINQ read models, value conversion, the Identity store, transactions, and application-managed concurrency tokens; reviewed provider-specific raw SQL where EF cannot express or plan an operation correctly. Compiled queries, interceptors, and owned types are evidence-driven rather than mandatory. Error translation, DI registration. Implements the facade. | Abstractions, Domain |
| `JobTrack.Migrations` | `net10.0` | Ordered, forward-only SQL migrations + a runner (e.g. DbUp); roles, extensions, reference data. Not model-generated. | — |
| `JobTrack.TestSupport` | `net10.0` | Typed test API: disposable Postgres (Testcontainers), scenario builders, deterministic clock/identity, raw "unsafe" helpers for integrity tests, plan-capture assertions. | Data, Migrations |
| `JobTrack.Web` | `net10.0` (ASP.NET Core) | Primary front end: HTTP API + UI, Identity, authorization, problem-details mapping. | Abstractions, Data (DI only) |
| `JobTrack.Cli` | `net10.0` | Secondary front end + admin/emergency tooling; consumes the same facade. | Abstractions, Data |

Front ends depend on `JobTrack.Abstractions` for types and take `JobTrack.Data`
only to call `services.AddJobTrack(...)`. They never see SQL, Npgsql, connection
strings (beyond configuration), or the domain internals.

### 12.2 Public facade

A single cohesive entry point (mainline scenarios require instantiating **one**
type — FDG "low barrier to entry"), split into cohesive sub-services to keep the
surface navigable and IntelliSense uncluttered. Methods follow the **Task-Based
Async Pattern**: `Async` suffix, return `Task`/`Task<T>`, and a trailing
`CancellationToken cancellationToken = default` — the *only* parameter permitted a
default on an interface method (FDG §5). They are pure request→response with no
ambient state:

```csharp
public interface IJobTrackClient
{
    IJobCommands      Jobs      { get; }   // structural mutations
    IWorkCommands     Work      { get; }   // sessions & leaf work
    IScheduleCommands Schedules { get; }   // schedules, exceptions, rates
    IJobQueries       Query     { get; }   // hierarchy, readiness, achievement
    ICostQueries      Costing   { get; }   // dynamic cost calculation
}
```

- **Commands** return a small result `record` (new id + new `version`), or throw
  (§12.6). Error conditions are never returned as codes or `null` (FDG ch. 7: throw,
  don't return error codes).
- **Queries** accept the least-specialised input type that works (`IEnumerable<T>`)
  and return immutable DTO `record`s and `IReadOnlyList<T>` — never `List<T>`,
  `Dictionary<,>`, or arrays in signatures, and never `null` (return an empty
  collection). Large or volatile result sets stream via `IAsyncEnumerable<T>` with
  `[EnumeratorCancellation]`.
- Expected, common "absence" outcomes use the **Try pattern**
  (`TryGetNode(NodeId, out …)`) rather than a thrown exception, per FDG; genuine
  failures throw.
- Every command carries a `CommandContext` (acting user id, roles, correlation id,
  and the `IClock`-supplied `asOf` where relevant). Authorization data is passed in
  by the front end; the library **enforces** the authorization it can (data scope,
  ownership) and the front end enforces policy at its boundary (§6.3).
- Requests use value objects — `NodeId`, `UserId`, `Instant`, `Money`,
  `TimeZoneId`, `Rate` — as `readonly record struct`s (FDG struct criteria: single
  value, <24 bytes, immutable), preventing argument transposition and centralising
  validation. Prefer these and enums over bare primitives and over multiple Boolean
  parameters (FDG §5: an enum beats `Foo(true, false)`).

Representative operations (non-exhaustive):

- **Jobs:** `AddChild`, `AttachLeafWork`, `MoveNode`, `SetOwner`,
  `SetPriority`, `Archive`, `DeleteUnusedPlanningNode`, `DecomposeWorkedLeaf`.
- **Work:** `StartSession`, `PauseSession`, `ResumeSession`, `FinishSession`,
  `CorrectSession`, `SetAchievement`.
- **Schedules/rates:** `AddScheduleVersion`, `AddException` (incl. overtime with
  optional rate), `SetUserCostRate`, `SetNodeRateOverride`.
- **Query:** `GetSubtree`, `GetAncestry`, `GetReadyNodes`, `GetAchievement`,
  `GetWorkPlan`, `GetOpenSessions`.
- **Costing:** `CostOfNode(nodeId, asOf?)`, `CostOfUser(userId, from, to)`,
  `CostBreakdown(nodeId, asOf?)` — the last returns per-session, per-segment lines
  with allocated hours, resolved rate, **rate provenance** (which override /
  user-rate / overtime supplied it), and any block flags.

### 12.3 Connection, transaction & unit-of-work model

- Postgres access uses a single pooled **`NpgsqlDataSource`** registered as a
  singleton (configured once: connection string from secret store, application
  name, command timeout, `SearchPath`, min/max pool). SQLite uses a configured
  connection factory.
- One logical operation = one connection = one transaction. Commands open a
  transaction, run their reads-and-writes, validate, and commit; any failure rolls
  back. Multi-statement structural operations (move, decompose) run inside a
  single transaction relying on **deferred** constraints for final validation
  (Postgres); SQLite uses ordered writes in an immediate-write transaction.
- **Cost reads run at `REPEATABLE READ`** (Postgres) with a single captured
  `asOf`, so hierarchy/sessions/schedules/prerequisites/rates come from one
  consistent snapshot (§10.3).
- An internal `IUnitOfWork` abstraction owns the connection/transaction lifetime so
  repositories compose within one transaction; it is not part of the public
  surface (front ends express intent through commands, not transactions).
- **Optimistic concurrency:** mutating commands accept the caller's `version`;
  `UPDATE ... WHERE id=@id AND version=@version` and a 0-row result raises
  `ConcurrencyConflictException`. `version` is an application-managed `bigint`, incremented
  on every write — Postgres transaction internals are never the public contract.
- Structural, tree-wide operations that must serialise (e.g. re-parenting) take a
  Postgres **advisory lock** keyed by the affected root/subtree to avoid racing
  concurrent restructures; SQLite serialises via the write transaction.

### 12.4 The cost engine

- The canonical implementation is a **pure function** in `JobTrack.Domain`:
  `CostEngine.Allocate(CostInputs inputs, Instant asOf) → CostResult`, where
  `CostInputs` is the fully-materialised, immutable set of sessions, effective
  working intervals, rate timelines, and prerequisite-block flags for the relevant
  workers. It is deterministic, side-effect-free, and fully unit-testable without a
  database — the boundary-partition sweep of §10.3 lives here.
- `JobTrack.Data` supplies `CostInputs` by executing the discovery queries
  (§10.2.2) inside the repeatable-read snapshot, then hands them to the engine.
- **For scale**, a Postgres-side SQL implementation of the same sweep (set-based,
  Appendix C) may be used to avoid materialising very large candidate sets in
  process. It is an optimisation: the in-process pure engine is the reference, and
  a conformance test asserts the SQL path and the engine produce identical
  allocations on the golden dataset. The blocked-but-open and equal-`1/N` rules
  (§10.2) are honoured identically by both.

### 12.5 SQL ownership & provider abstraction

- **EF Core 10 is the single general-purpose data-access technology** for change
  tracking, LINQ, value conversion, the Identity store, and application-managed
  concurrency tokens. Compiled queries, interceptors, and owned types are used only
  where measurement or a demonstrated cross-cutting requirement justifies them. Where EF cannot express or
  plan an operation correctly, provider-specific raw SQL lives beside the code that
  runs it (embedded resources / raw string constants) and is parameterised. EF Core
  *migrations* are **not** the schema mechanism — the authoritative schema uses
  PostgreSQL features EF migrations cannot represent (GiST exclusion constraints,
  deferred constraint triggers, range types) and is deployed as forward-only,
  checksum-verified SQL by `JobTrack.Migrations`.
- Provider differences (`EXCLUDE` vs trigger, `now()` vs epoch-int, boolean
  encoding, `RETURNING` support, upsert syntax) are isolated behind provider
  strategies selected at registration. Shared SQL is defined once; only the
  divergent statements are duplicated.
- All access is **parameterised** — no string-concatenated SQL anywhere. Identifier
  interpolation (if ever needed) uses an allow-list, never user input.

### 12.6 Error model (FDG ch. 7)

- **Usage errors throw framework exceptions directly** — do not invent a custom
  type for a mistake a caller cannot handle differently (FDG): `ArgumentNullException`
  / `ArgumentException` / `ArgumentOutOfRangeException` (with `ParamName`) for bad
  arguments, `InvalidOperationException` for wrong object/aggregate state,
  `OperationCanceledException` for cancellation, `ObjectDisposedException` after
  disposal. The public API never surfaces `NullReferenceException` etc.
- **A shallow custom hierarchy** (FDG "avoid deep hierarchies"; `Exception` suffix;
  `()`, `(string)`, `(string, Exception)` constructors) exists only for
  program-error conditions a caller *would* handle differently, in
  `JobTrack.Abstractions`: `JobTrackException` (base) →
  `ConcurrencyConflictException`, `PrerequisiteBlockedException`,
  `MissingRateException`, `EntityNotFoundException`, `AuthorizationException`, and
  `InvariantViolationException` (carrying a stable `ConstraintId`). Each maps to a
  distinct front-end response (409, 409/422, 422, 404, 403, 409).
- Contract-violation exceptions each member can throw are **documented as part of
  the member's contract**; changing them is a compatibility change (FDG).
- Postgres `SqlState` + **stable constraint identifiers** are translated to these at
  the data boundary — never message-text matching — so a race that trips a DB
  constraint surfaces as the same typed exception the pre-check would have raised
  (e.g. `ex_no_same_leaf_user_overlap` → `InvariantViolationException("session_overlap")`).
- **No error codes, no silent fallbacks, no `NULL`-on-error sentinels** (the legacy
  `GetSubst` `@i > 50 → NULL` pattern is explicitly disallowed). Every failure is a
  thrown exception or a rollback. Expected-and-common absence is offered instead
  through a `Try*` member (§12.2), so callers avoid the throw on the hot path.

### 12.7 Cross-cutting concerns

- **Async & cancellation** end-to-end; long cost reports honour cancellation and a
  bounded statement timeout.
- **Logging/telemetry:** `ILogger` + OpenTelemetry activity per command/query
  (operation name, actor, correlation id, affected ids, row counts, duration) —
  **never** secrets, rates, or PII in logs.
- **Configuration/secrets:** connection strings and the least-privilege
  application role come from environment / secret store; nothing is hard-coded
- **Least privilege:** the application role can only `SELECT/INSERT/UPDATE` the
  intended tables and `EXECUTE` intended functions; DDL/`DELETE` on cost-relevant
  tables is withheld. The emergency-reset role (§6.2) is separate.
- **Startup safety:** the library never auto-creates or silently mutates schema;
  migration is a separate, explicitly-authorized step (`JobTrack.Migrations`).

### 12.8 Front-end integration

- One registration extension: `services.AddJobTrack(options => { options.UsePostgres(connName); /* or */ options.UseSqlite(path); options.Clock = ...; })` wires the data source, provider strategy, clock, repositories, and the facade.
- **ASP.NET Core (primary):** minimal-API/controller endpoints map 1:1 to facade
  commands/queries; Identity + policy authorization at the boundary; a
  `CommandContext` is built from the authenticated principal; all errors map to
  RFC-7807 problem-details with `ConstraintId`/error-code, ISO-8601 offsets, and
  descriptive status names — **no DB ids or legacy one-char codes leak**. The UI is
  one client of that API.
- **Other front ends:** the CLI, worker/report services, and future clients call
  the same `IJobTrackClient` directly with their own `CommandContext`, so business
  behaviour is identical regardless of entry point. Admin/emergency tooling
  (bootstrap first admin, emergency reset) lives in the CLI using the separate
  least-privilege role.
- App project layering (Domain / Application / Infrastructure / Migrator / Web /
  Tests) holds no SQL and no rules that belong in the library.

### 12.9 Framework Design Guidelines conformance

The public surface is treated as a long-term compatibility commitment and designed
per Cwalina & Abrams (4th ed.); the essentials are in
`Framework_Design_Guidelines_Essentials.md`. Binding rules for this library:

- **Scenario-driven, sample-first (ch. 2 / App. C).** The public API is specified
  by the consumer sample code in Appendix B *first*; the object model exists to make
  those call sites clear and economical. Each significant feature keeps a minimal
  API spec: main scenarios as consumer code, goals/non-goals, surface + behaviour,
  and failure semantics. Design the **pit of success** — the correct, secure path
  is the easiest one; misuse takes deliberate effort.
- **Layering (ch. 2).** The high-level facade (productivity) and the low-level
  cost engine / provider strategies (power) are separate; advanced types live in
  sub-namespaces, never cluttering the mainline `IJobTrackClient` surface.
- **Naming (ch. 3).** PascalCase types/members, camelCase parameters; interfaces
  are `I`-prefixed adjective/noun phrases; no `C`/`Base`/`Ex` prefixes or suffixes;
  no Hungarian, no underscores, ASCII only; readable over terse
  (`CanStartSession`, not `CanStrt`). Namespaces are `JobTrack[.Feature]`, a stable
  product name — not org structure. Use the established term pairs (`Add`/`Remove`,
  `Start`/`Finish`, `Get`/`Set`) consistently and never mix antonym sets.
- **Types (ch. 4).** Prefer classes; interfaces used for the facade contract and
  provider seams (each ships ≥1 implementation + ≥1 consumer). Value objects are
  `readonly record struct` only where all struct criteria hold; DTOs are `record`
  classes with value semantics reviewed as public API. Enums are singular, `Int32`,
  with a sensible zero (`Achievement.Waiting`); no `[Flags]` unless genuinely a
  bit-set; no enums for open/extensible sets (those are lookup tables).
- **Members (ch. 5).** Longest overload does the work, shorter ones delegate; no
  overloading on `ref`/`out`; properties only for cheap in-memory attributes (cost
  results are **methods**, not properties — they do I/O and vary per call); get-only
  where callers must not mutate; validate all public arguments; prefer an enum to
  ≥2 Booleans; consistent parameter names/order across overloads and overrides.
- **Extensibility (ch. 6).** Every abstraction is a lifetime contract: keep the
  facade interfaces minimal and stable; extension points (provider strategy, clock)
  are validated by concrete implementations before they ship. Don't add virtuals or
  interface members speculatively.
- **Exceptions (ch. 7).** As §12.6 — throw, don't return codes; specific existing
  types for usage errors; a shallow custom hierarchy only for distinctly-handled
  program errors; `Try*` for expected absence; developer-targeted messages ending in
  a period, no security-sensitive detail.
- **Async & common types (ch. 8–9).** TAP with `Async` suffix and a defaulted
  trailing `cancellationToken`; validate synchronously before returning the task;
  `ConfigureAwait(false)` throughout the library; `IAsyncEnumerable<T>` with
  `[EnumeratorCancellation]`; `DateTimeOffset` for instants; `IReadOnlyList<T>` /
  `IEnumerable<T>` in signatures, never concrete collections; `IDisposable`/
  `IAsyncDisposable` on the data source wrapper via the standard dispose pattern.
- **Compatibility (App. D).** Post-1.0 evolution is additive by default; renames,
  removals, signature/const/default changes, and added interface members are
  breaking — prefer `[Obsolete]` + replacement over mutating a shipped contract.
- **Implementation house style (App. A).** Allman braces, four-space indent,
  language keywords over BCL names, `_`/`s_` field prefixes, explicit visibility
  first, one public type per file — governs internal code; the rules above govern
  the surface.

---

## 13. Testing

Test-driven; tests precede implementation. Run all test commands under `gtimeout`.

- **Domain unit tests:** classification (leaf/branch/root); LeafWork with 0/1/many
  sessions; pause/resume without tree mutation; multiple users on one leaf;
  rejection of same-(user,leaf) overlap; atomic decomposition preserving sessions;
  recursive branch success; empty/unsuccessful leaves; nearest-ancestor
  effective-dated override resolution incl. boundaries/gaps/overlap rejection;
  half-open boundaries; equal `1/N` allocation incl. large `N`; differing overrides
  and overtime rate on concurrent work; unfinished work with fixed `asOf`;
  schedules crossing midnight, multiple daily intervals, historical versions;
  additive overtime + subtractive leave + their overlap; DST gap/ambiguous times;
  dynamic historical recalculation; **cost of an open prerequisite-blocked session
  (must be non-zero and counted in `N`)**; rounding policy.
- **Property tests:** interval normalisation; **allocation conservation** — for any
  user and interval, allocated time across sessions equals eligible worked time and
  never exceeds it (money is neither created nor destroyed by the split).
- **Database integration (both engines):** reject a second root; parent cycles;
  orphans; LeafWork on root/branch; child added to a LeafWork node outside
  decomposition; same-(user,leaf) session overlap; missing ownership;
  starting/completing prerequisite-blocked work; deletion of cost-relevant jobs;
  duplicate/cyclic/ancestor-descendant prerequisites; overlapping rate ranges;
  concurrently-submitted writes that jointly violate an invariant. Testcontainers
  for Postgres; file-based SQLite; real query-plan/index-use assertions;
  constraint-identifier-based error assertions (not message text).
- **Auth/authz tests:** employee-only provisioning; login without enumeration;
  lockout/rate-limit; hashing + password-change invalidation; normal + emergency
  reset; forced change + single-use expiry; session revocation; antiforgery;
  default-deny; each role + combination; worker subtree scope and own-session /
  own-schedule limits under direct HTTP; independent rate/cost visibility;
  projection omitting unauthorized fields; append-only secret-free audit.
- **End-to-end:** create/restructure jobs, start/finish work, prerequisite gating,
  achievement propagation, schedule/rate admin, overtime, cost reporting.

Deterministic golden scenarios assert exact hierarchy/achievement/overlap/rate-
provenance/GBP results; generated reproducible datasets include 20+/100+/deep/
broad/long-history/many-user concurrency cases.

---

## 14. Seed & Reference Data (greenfield — no migration)

No legacy data is migrated; the system starts empty. Two data classes are authored
as source-controlled SQL, kept strictly separate:

- **Reference data** (deployed with the schema, part of every environment):
  `achievement_status` (with canonical name and terminal-state metadata), `priority`,
  `rate_kind`, `access_level`, and the seeded role definitions. These are stable
  domain vocabulary, not test fixtures.
- **Seed / scenario data** (development, demo, and test only; never production):
  the deterministic golden scenarios of §13 — a multi-level tree with one root;
  leaves in every achievement state; a branch achieved only when all children are;
  a prerequisite chain and a deliberate would-be cycle (for negative tests); users
  in different IANA zones; effective-dated schedules crossing a DST boundary; an
  overtime exception carrying its own rate; effective-dated user rates and inherited
  node overrides; and a worker with 3+ overlapping open sessions (incl. one on a
  prerequisite-blocked job) to exercise `1/N` allocation and the blocked-but-open
  rule. Golden scenarios assert exact hierarchy / achievement / overlap /
  rate-provenance / GBP results.

Production startup deploys reference data only, via the explicit, separately
authorized migration step (`JobTrack.Migrations`) — never scenario data, never an
auto-created schema, never a default account.

---

## 15. Decisions Log

Resolved decisions are recorded below. Achievement transitions, historical
schedule/rate correction policy, and hierarchy-display penny reconciliation remain
open product decisions and must be reflected in the authoritative specification
before the database schema is frozen.

1. **Rate model** — the Codex separation: effective-dated schedules,
   effective-dated `user_cost_rate`, and effective-dated node overrides with
   nearest-ancestor inheritance. (Supersedes the earlier "combined time+rate
   intervals" idea.)
2. **Overtime** — an `AddWorkingTime` schedule exception that **may carry its own
   explicit rate** (§8.3), and that rate **supersedes node overrides** (§9.3): the
   overtime rate is precedence rank 1.
3. **Blocked-but-open sessions** — prerequisites gate *starting/completing* work
   only. An already-open session on a blocked job **incurs cost and counts in `N`**.
   This agrees with the authoritative specification; it is not an override.
4. **No migration** — greenfield; the legacy DB informs the design but no data is
   carried over. Reference + seed data per §14.
5. **Legacy features retired** — `substitute_node_id`, `continues_sibling`,
   `allows_leaves`, stored UI view state, and the legacy ASP.NET DB users/schemas
   are **not** carried into the design.
6. **Build order** — database → library → front end (§1); this document is
   specification only, no implementation.
7. Earlier confirmed: only `WorkSession` incurs cost; UTC instants + per-user IANA
   zone; equal `1/N` time split for all open sessions; unfinished work → `asOf`;
   installation-wide GBP with banker's rounding; public API per Framework Design
   Guidelines (§12.9).

---

## 16. Acceptance Criteria

The system is acceptable when: all hierarchy/prerequisite invariants hold under
concurrent writes; only `WorkSession`s contribute cost; overnight in-progress work
produces no cost outside effective working intervals; overtime exceptions enable
out-of-hours cost (at their own rate when set); concurrent work receives equal
`1/N` time shares for any `N` with no fixed limit; inherited overrides + precedence
are deterministic (overtime rate outranks node overrides); branch achievement and
prerequisite satisfaction are recursively correct; all calculations are
timezone/DST-safe; historical schedule/rate changes dynamically affect costs; open
prerequisite-blocked sessions still cost and count in `N` while remaining impossible
to start/complete; tests pass against both engines; cost is reproducible given the
same state + `asOf`; the public library surface conforms to the Framework Design
Guidelines (§12.9); no unauthenticated access to employee/job data; every sensitive
operation is behind an explicit authorization policy at the app boundary that holds
under direct HTTP; a job's direct owner is optional (unassigned pool, except the
permanent root) and worker mutation is restricted to nodes they control; admins can securely provision/disable/revoke/reset accounts without
reusable secrets; completed/cost-relevant jobs are retained and only archivable; and
all monetary values/reports use GBP with the defined rounding.

---

## Appendix A — PostgreSQL DDL Sketch (illustrative)

```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE job_node (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    parent_id     bigint REFERENCES job_node(id) ON DELETE RESTRICT,
    description   text NOT NULL CHECK (length(btrim(description)) > 0),
    writeup       text,
    posted_by_user_id bigint NOT NULL REFERENCES app_user(id),
    owner_user_id     bigint REFERENCES app_user(id), -- NULL = unassigned pool; root requires non-null
    priority      int  NOT NULL REFERENCES priority(id),
    -- kind (Root/Branch/Leaf) is derived at read time from parent_id and child existence (ADR 0035);
    -- it is not stored on job_node.
    expected_duration_hours numeric(18,2) CHECK (expected_duration_hours >= 0),
    expected_cost numeric(19,6) CHECK (expected_cost >= 0),
    needed_start  timestamptz,
    needed_finish timestamptz,
    posted_at     timestamptz NOT NULL DEFAULT now(),
    archived_at   timestamptz,
    version       bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_not_own_parent CHECK (parent_id IS NULL OR parent_id <> id),
    CONSTRAINT ck_needed_dates CHECK (needed_finish IS NULL OR needed_start IS NULL
                                      OR needed_finish > needed_start)
);
CREATE UNIQUE INDEX ux_single_root ON job_node ((parent_id IS NULL)) WHERE parent_id IS NULL;
CREATE INDEX ix_job_node_parent ON job_node(parent_id);
CREATE INDEX ix_job_node_owner  ON job_node(owner_user_id, archived_at);

CREATE TABLE leaf_work (
    job_node_id  bigint PRIMARY KEY REFERENCES job_node(id) ON DELETE RESTRICT,
    achievement          int NOT NULL REFERENCES achievement_status(id),
    partial_criteria text,
    full_criteria    text,
    changed_at   timestamptz NOT NULL DEFAULT now(),
    version      bigint NOT NULL DEFAULT 1
);

CREATE TABLE work_session (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leaf_work_id bigint NOT NULL REFERENCES leaf_work(job_node_id) ON DELETE RESTRICT,
    worked_by_user_id bigint NOT NULL REFERENCES app_user(id),
    started_at   timestamptz NOT NULL,
    finished_at  timestamptz,
    changed_at   timestamptz NOT NULL DEFAULT now(),
    version      bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_session_order CHECK (finished_at IS NULL OR finished_at > started_at),
    CONSTRAINT ex_no_same_leaf_user_overlap
        EXCLUDE USING gist (
            worked_by_user_id WITH =,
            leaf_work_id WITH =,
            tstzrange(started_at, COALESCE(finished_at, 'infinity')::timestamptz, '[)') WITH &&
        )
);
CREATE INDEX ix_session_user_start ON work_session(worked_by_user_id, started_at);
CREATE INDEX ix_session_user_fin   ON work_session(worked_by_user_id, finished_at);
-- plus a GiST index on (worked_by_user_id, tstzrange(...)) for overlap discovery.

CREATE TABLE user_cost_rate (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id     bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    effective   tstzrange NOT NULL,
    rate        numeric(19,6) NOT NULL CHECK (rate >= 0),
    EXCLUDE USING gist (user_id WITH =, effective WITH &&)
);

CREATE TABLE node_rate_override (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    node_id     bigint NOT NULL REFERENCES job_node(id) ON DELETE RESTRICT,
    user_id     bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    effective   tstzrange NOT NULL,
    rate        numeric(19,6) NOT NULL CHECK (rate >= 0),
    EXCLUDE USING gist (node_id WITH =, user_id WITH =, effective WITH &&)
);

CREATE TABLE user_schedule_exception (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id     bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    span        tstzrange NOT NULL CHECK (NOT isempty(span)),
    effect      int NOT NULL,           -- add | remove (lookup/enum)
    rate_override numeric(19,6) CHECK (rate_override IS NULL OR rate_override >= 0),
    reason      text NOT NULL,
    created_by  bigint NOT NULL REFERENCES app_user(id)
);

CREATE TABLE job_prerequisite (
    required_job_id bigint NOT NULL REFERENCES job_node(id) ON DELETE RESTRICT,
    dependent_job_id bigint NOT NULL REFERENCES job_node(id) ON DELETE RESTRICT,
    PRIMARY KEY (required_job_id, dependent_job_id),
    CONSTRAINT ck_prereq_distinct CHECK (required_job_id <> dependent_job_id)
);
```

Deferred constraint triggers (single root, tree acyclicity/reachability,
leaf⊕branch, prereq DAG + ancestor/descendant exclusion) and their SQLite trigger
equivalents are defined in the migration scripts.

---

## Appendix B — Library Public Surface (illustrative C#)

```csharp
// ---- value objects (JobTrack.Abstractions) ----
public readonly record struct NodeId(long Value);
public readonly record struct UserId(long Value);
public readonly record struct Rate(decimal PerHour);            // GBP, >= 0
public readonly record struct Money(decimal Amount);            // GBP
public readonly record struct Instant(DateTimeOffset Utc);      // always UTC
public readonly record struct TimeZoneId(string Iana);

public enum Achievement { Waiting, InProgress, NotAchieved, Partial, Success }
public enum ScheduleExceptionEffect { AddWorkingTime, RemoveWorkingTime }

public sealed record CommandContext(
    UserId Actor, IReadOnlySet<string> Roles, Guid CorrelationId, Instant AsOf);

// ---- commands (TAP: Async suffix; only cancellationToken is defaulted) ----
public interface IWorkCommands
{
    Task<SessionCreated> StartSessionAsync(
        StartSession command, CommandContext context, CancellationToken cancellationToken = default);
    Task<SessionUpdated> FinishSessionAsync(
        FinishSession command, CommandContext context, CancellationToken cancellationToken = default);
    Task<SessionUpdated> CorrectSessionAsync(
        CorrectSession command, CommandContext context, CancellationToken cancellationToken = default);
    // pause = finish current; resume = start new; neither mutates the tree
}

public sealed record StartSession(NodeId LeafNode, UserId Worker);      // rechecks prereq gate
public sealed record FinishSession(long SessionId, long Version, Achievement? Result);
public sealed record CorrectSession(long SessionId, long Version, Instant Started, Instant? Finished, string Reason);
public sealed record SessionCreated(long SessionId, long Version);
public sealed record SessionUpdated(long Version);

// ---- costing (methods, not properties: they do I/O and vary per call) ----
public interface ICostQueries
{
    Task<Money> CostOfNodeAsync(
        NodeId node, Instant? asOf, CommandContext context, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CostLine>> CostBreakdownAsync(
        NodeId node, Instant? asOf, CommandContext context, CancellationToken cancellationToken = default);
}

public sealed record CostLine(
    long SessionId, NodeId LeafNode, UserId Worker,
    Instant SegmentStart, Instant SegmentEnd,
    double AllocatedHours, int ConcurrentN,
    Rate ResolvedRate, RateSource Source, Money Amount,
    bool PrerequisiteBlocked);

public enum RateSource { OvertimeException, NodeOverride, UserCostRate, UserDefault }

// ---- exception hierarchy (shallow; Exception suffix; standard ctors) ----
public class JobTrackException : Exception { /* (), (string), (string, Exception) */ }
public sealed class ConcurrencyConflictException : JobTrackException { }   // -> 409
public sealed class PrerequisiteBlockedException : JobTrackException { }   // -> 409/422
public sealed class MissingRateException         : JobTrackException { }   // -> 422
public sealed class EntityNotFoundException      : JobTrackException { }   // -> 404
public sealed class AuthorizationException       : JobTrackException { }   // -> 403
public sealed class InvariantViolationException  : JobTrackException       // -> 409/422
{ public string ConstraintId { get; } }
// usage errors throw framework types directly: ArgumentException, ArgumentNullException,
// ArgumentOutOfRangeException, InvalidOperationException, OperationCanceledException.

// ---- pure engine (JobTrack.Domain) ----
public static class CostEngine
{
    // deterministic; no I/O; reference implementation of s.10.3
    public static CostResult Allocate(CostInputs inputs, Instant asOf);
}
```

Error translation at the data boundary maps Postgres `SQLSTATE` + stable constraint
name to this hierarchy, e.g. `ex_no_same_leaf_user_overlap` →
`InvariantViolationException("session_overlap")`, `unique_violation` on a rate range
→ `InvariantViolationException("rate_overlap")`, a 0-row optimistic update →
`ConcurrencyConflictException`. Expected-and-common absence uses a `Try*` member and
returns `false` rather than throwing.

---

## Appendix C — Core Reference SQL (Postgres, illustrative)

### C.1 Derived achievement (recursive)

```sql
-- Leaf success uses canonical Success; no ordering threshold is involved.
CREATE VIEW v_leaf_success AS
SELECT lw.job_node_id,
       (a.name = 'Success') AS succeeded
FROM leaf_work lw
JOIN achievement_status a ON a.id = lw.achievement;

-- Node success: leaves from v_leaf_success; a branch succeeds iff every child does.
-- Evaluated bottom-up; a node with no leaf_work and no children never succeeds.
CREATE FUNCTION node_succeeded(p_node bigint) RETURNS boolean
LANGUAGE sql STABLE AS $$
  WITH RECURSIVE agg AS (
    -- children roll-up computed via a recursive descent; see migration for the
    -- full closure form. Conceptually:
    SELECT bool_and(child_ok) FROM (
      SELECT node_succeeded(c.id) AS child_ok
      FROM job_node c WHERE c.parent_id = p_node
    ) k
  )
  SELECT COALESCE(
    (SELECT succeeded FROM v_leaf_success WHERE job_node_id = p_node),
    (SELECT CASE WHEN EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = p_node)
                 THEN (SELECT * FROM agg) ELSE false END)
  );
$$;
-- Production form uses a single set-based recursive CTE (no per-row recursion);
-- shown recursively here only for readability.
```

### C.2 Readiness (all prerequisites of node + ancestors satisfied)

```sql
CREATE VIEW v_node_ready AS
SELECT n.id AS node_id,
       NOT EXISTS (
         SELECT 1
         FROM (  -- node's own prereqs plus inherited ancestor prereqs
           WITH RECURSIVE anc(id) AS (
             SELECT n.id
             UNION ALL SELECT p.parent_id FROM job_node p JOIN anc ON p.id = anc.id
                       WHERE p.parent_id IS NOT NULL
           )
           SELECT jp.required_job_id
           FROM job_prerequisite jp JOIN anc ON jp.dependent_job_id = anc.id
         ) req
         WHERE NOT node_succeeded(req.required_job_id)
       ) AS ready
FROM job_node n;
```

### C.3 Tree acyclicity (deferred constraint trigger)

```sql
CREATE FUNCTION trg_job_node_acyclic() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.parent_id IS NOT NULL THEN
    IF NEW.parent_id = NEW.id
       OR EXISTS (
         WITH RECURSIVE up(id) AS (
           SELECT NEW.parent_id
           UNION ALL SELECT p.parent_id FROM job_node p JOIN up ON p.id = up.id
                     WHERE p.parent_id IS NOT NULL
         )
         SELECT 1 FROM up WHERE id = NEW.id            -- new parent reaches back to us
       )
    THEN RAISE EXCEPTION 'job_node cycle' USING ERRCODE='23514', CONSTRAINT='ck_tree_acyclic';
    END IF;
  END IF;
  RETURN NEW;
END $$;

CREATE CONSTRAINT TRIGGER ct_job_node_acyclic
  AFTER INSERT OR UPDATE OF parent_id ON job_node
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION trg_job_node_acyclic();
```

An analogous deferred constraint trigger on `job_prerequisite` rejects a new edge
when `dependent_job_id` can already reach `required_job_id` (cycle) or when either
endpoint is an ancestor/descendant of the other. A trigger on `job_node` /
`leaf_work` enforces leaf⊕branch exclusivity (no `leaf_work` on a node that has
children or is the root; no child under a node that has `leaf_work`).

### C.4 Cost sweep (set-based boundary partition)

```sql
-- Given @user, @from, @to, @asof: allocate rate/N per constant segment.
WITH sess AS (   -- all of the user's sessions overlapping the window
  SELECT s.id, s.leaf_work_id AS node_id,
         GREATEST(s.started_at, @from) AS a,
         LEAST(COALESCE(s.finished_at, @asof), @to) AS b
  FROM work_session s
  WHERE s.worked_by_user_id = @user
    AND s.started_at < @to
    AND COALESCE(s.finished_at, @asof) > @from
),
work_ivl AS (    -- sessions clipped to effective working set (schedule +/- exceptions)
  SELECT sess.id, sess.node_id, x.a, x.b
  FROM sess CROSS JOIN LATERAL clip_to_working_set(@user, sess.a, sess.b) AS x(a,b)
),
bounds AS (      -- every start/end + rate boundary within the window, sorted, distinct
  SELECT DISTINCT t FROM (
    SELECT a AS t FROM work_ivl UNION SELECT b FROM work_ivl
    UNION SELECT lower(effective) FROM user_rate_boundaries(@user, @from, @to)
    UNION SELECT upper(effective) FROM user_rate_boundaries(@user, @from, @to)
  ) u WHERE t >= @from AND t <= @to
),
seg AS (         -- consecutive boundary pairs
  SELECT t AS seg_start, lead(t) OVER (ORDER BY t) AS seg_end FROM bounds
)
SELECT wi.id AS session_id, wi.node_id, seg.seg_start, seg.seg_end,
       count(*) OVER (PARTITION BY seg.seg_start) AS n,        -- active sessions in segment
       resolve_rate(wi.node_id, @user, seg.seg_start) AS rate,
       EXTRACT(EPOCH FROM (seg.seg_end - seg.seg_start))/3600.0
         / count(*) OVER (PARTITION BY seg.seg_start)          -- allocated hours = dur / N
         AS alloc_hours
FROM seg
JOIN work_ivl wi
  ON wi.a < seg.seg_end AND wi.b > seg.seg_start               -- session active in segment
WHERE seg.seg_end IS NOT NULL;
-- Amount = alloc_hours * rate; aggregate to leaf/branch/root. Blocked-but-open
-- sessions are included (they cost and count in N, per s.10.2); resolve_rate()
-- applies the s.9.3 precedence incl. the overtime-exception rate.
```

`clip_to_working_set`, `user_rate_boundaries`, and `resolve_rate` are the
schedule/rate helper functions specified in §8–§9; the in-process `CostEngine`
implements the identical logic and is the conformance reference.

*End of specification.*
