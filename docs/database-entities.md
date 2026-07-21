# Core database entities and the costing algorithm

An orientation to JobTrack's central data model and how it turns a hierarchy of jobs and work
sessions into a cost. This is not exhaustive schema documentation — see
`database/postgresql/schema-versions/` for every table and constraint, and
`docs/jobtrack_spec_codex.md` for the normative specification. Column types below are PostgreSQL's
(the primary, authoritative provider); the SQLite provider (`database/sqlite/schema-versions/`)
is a fully conforming secondary provider with the same invariants, encoded with TEXT/INTEGER
workarounds and triggers where PostgreSQL uses native types and `EXCLUDE` constraints.

## The job hierarchy: `job_node`

Every job is a row in `job_node`, forming a single-rooted tree via `parent_id`:

| Column | Type | Notes |
|---|---|---|
| `id` | `bigint identity` | |
| `parent_id` | `bigint`, nullable | Null only for the root. `ON DELETE RESTRICT`. |
| `description` | `text` | Non-blank. |
| `write_up` | `text`, nullable | |
| `posted_by_user_id` | `bigint` | FK `app_user`. |
| `owner_user_id` | `bigint`, nullable | FK `app_user`. `0..1` — `NULL` means unassigned (the pickup pool), except on the permanent root, which must always have a non-null owner (ownership model, ADR 0031). |
| `expected_duration_hours` | `numeric(18,2)`, nullable | |
| `expected_cost` | `numeric(19,6)`, nullable | |
| `needed_start`, `needed_finish` | `timestamptz`, nullable | `needed_finish > needed_start` if both set. |
| `priority_id` | `smallint` | FK `priority` (Low/Medium/High/Urgent). |
| `posted_at`, `archived_at` | `timestamptz` | `archived_at` nullable. |
| `row_version` | `bigint` | Optimistic concurrency token. |

**Hierarchy invariants**, all enforced at the database, not just the application:

- **Exactly one root, always.** A partial unique index on `(parent_id IS NULL)` permits at most one
  row with a null parent, at every instant — not just at steady state.
- **The root is permanent** (ADR 0015). Once installation is complete, triggers reject any attempt
  to delete or re-parent the root row; its other columns can still be edited normally.
- **No self-parenting** and **no cycles** — a deferred constraint trigger walks the new parent's
  ancestor chain on every `parent_id` update and rejects a move that would make the moved node its
  own ancestor. Because every non-root row has exactly one parent (via a NOT NULL-enforced FK to an
  existing row) and there's exactly one root, acyclicity alone is enough to guarantee every node is
  reachable from the root — there's no separate "reachability" check to maintain.
- **Concurrency-safe moves.** A bare `UPDATE ... SET parent_id` is not safe under concurrent writers;
  the canonical move path takes PostgreSQL advisory locks, in ascending key order, on the moving
  node and both its old and new ancestor chains, plus a fixed lock shared with prerequisite-edge
  writes (see below) — closing a proven race where a concurrent move and a concurrent prerequisite
  insert can each pass validation individually but jointly violate an invariant.

**Leaf/branch exclusivity** (spec §4.2, rules 7-10) — a node cannot be both a branch and hold work.
**Root/Branch/Leaf** is not stored: it is a contextual read label derived from `parent_id` and child
existence (`parent_id IS NULL` → Root; otherwise has children → Branch; otherwise → Leaf). See ADR
0035.

1. the root cannot hold `LeafWork`;
2. a branch (≥1 children) cannot hold `LeafWork`;
3. a leaf has no children, and zero-or-one `LeafWork`;
4. a node can never gain a child *and* `LeafWork` at once.

These are also enforced by deferred constraint triggers (so a multi-step structural operation —
e.g. decomposing a leaf into a branch with new leaf children — can complete in any statement order
within one transaction, and only the final state has to satisfy the rule).

## Work: `leaf_work` and `work_session`

A leaf that has actual work attached gets a `leaf_work` row — a strict 1:1 extension of `job_node`
(its primary key *is* the foreign key to `job_node`, which is what makes "zero-or-one per leaf" a
schema-level guarantee, not an application check):

| Column | Type | Notes |
|---|---|---|
| `job_node_id` | `bigint` | PK and FK to `job_node`. |
| `achievement_id` | `smallint` | FK `achievement_status`, defaults to `Waiting`. |
| `partial_criteria`, `full_criteria` | `text`, nullable | |
| `changed_at`, `row_version` | | |

**Achievement** works differently at the leaf and the branch level:

- **A leaf's achievement is stored and manually set** — `Waiting → InProgress → {Success, Cancelled,
  Unsuccessful}`, or `Waiting → {Cancelled, Unsuccessful}` directly. Any terminal state can be
  reopened back to `Waiting`, but only by a Job manager or Administrator, with a mandatory audited
  reason (ADR 0001). Only `Success` satisfies a prerequisite.
- **A branch's (and the root's) achievement is never stored** — it's always derived: a branch
  succeeds if and only if every direct child succeeds, recursively, all the way down to leaves. A
  cached value for query performance is disposable and must be rebuildable, never authoritative.

The five `Achievement` values, in permitted-transition order: `Waiting`, `InProgress`, `Success`,
`Cancelled`, `Unsuccessful` (`JobTrack.Abstractions.Achievement`; the numeric values match the
seeded `achievement_status` reference table exactly, so persistence needs no translation table).

A `leaf_work` row can have any number of `work_session` rows — actual clocked time, not one row per
leaf (the legacy system's model):

| Column | Type | Notes |
|---|---|---|
| `id` | `bigint identity` | |
| `leaf_work_id` | `bigint` | FK `leaf_work`. |
| `worked_by_user_id` | `bigint` | FK `app_user`. |
| `started_at` | `timestamptz` | |
| `finished_at` | `timestamptz`, nullable | Null = still active/open. |
| `session_range` | `tstzrange`, generated | `[started_at, finished_at or infinity)`, stored. |
| `changed_at`, `row_version` | | |

**Concurrency rule**: sessions for the same user *on the same leaf* must not overlap (a generated
`tstzrange` column plus a GiST `EXCLUDE` constraint on `(worked_by_user_id, leaf_work_id,
session_range)` enforces this — representing an open session's end as unbounded infinity, rather
than a magic sentinel value repeated at every call site, is what lets the exclusion constraint
treat "still working" as correctly overlapping any later session on the same leaf). Sessions for the
same user *on different leaves* are explicitly allowed to overlap — a user can legitimately have
several active sessions at once, and this is exactly what drives the concurrent cost-allocation
rule below.

**Cardinality**: one `leaf_work` row has **zero or more** `work_session` rows, and more than one of
those can be active (`finished_at IS NULL`) *simultaneously*, as long as each belongs to a different
`worked_by_user_id` — the per-user exclusion constraint above scopes non-overlap to one user, not to
the leaf as a whole. Several employees clocked in on the same leaf at once is a legitimate, supported
state; no query or view may assume "at most one active session per leaf."

**Closed-leaf creation guard (ADR 0044)**: a `work_session` row cannot be inserted or reactivated
(an update leaving `finished_at NULL`) while its leaf is **closed** — `leaf_work.achievement_id` is
terminal (`Success`/`Cancelled`/`Unsuccessful`) or the owning `job_node.archived_at` is set. An
*archived* leaf rejects any new row outright, active or already finished; a merely
terminal-achievement leaf rejects only a new active row (a new finished row remains insertable,
which is what lets subtree import record already-completed historical work and set the leaf's
terminal achievement in one transaction). Enforced by named deferred constraint triggers on both
providers, serialized per leaf against a concurrent terminal-achievement transition or archive via
one advisory-lock domain on PostgreSQL (ADR 0012) and SQLite's existing single-writer transaction
model — never by an application-only pre-check.

## Dependencies: `job_prerequisite`

A directed edge `(from_id, to_id)` means `from_id` is *required by* `to_id` — `RequiredJob →
DependentJob`. The dependent job's prerequisite is satisfied only once the required job's derived
achievement is `Success`.

Enforced structural rules: both endpoints must exist (FK), a node can't require itself, duplicate
edges are rejected (composite primary key), the edge set must stay acyclic, and an edge can never
connect two nodes that are already ancestor/descendant of each other in the job hierarchy — checked
both when the edge is inserted and, since a hierarchy move can create or remove such a relationship
elsewhere, re-checked across *every* existing prerequisite edge whenever any node moves. The
canonical edge-insert path takes the same fixed advisory lock the hierarchy-move path takes, closing
the same class of cross-domain race described above.

**Readiness as a command gate (spec §6).** A node is *ready* only when every prerequisite attached
to it or to any of its ancestors is satisfied (a branch prerequisite is inherited by the whole
subtree). This gates exactly two operations, each rechecking readiness live inside its own write
transaction so a prerequisite added after work began is still enforced: **starting** a leaf's work
session, and **completing** a leaf (its `LeafWork` entering a completed state such as `Success`).
Both throw `PrerequisiteBlockedException` on both providers. Deliberately *not* gated: **finishing a
work session** (stopping the clock records labour that happened — the spec keeps it ungated so
prerequisite regression can't trap an active worker; the recorded time stays costable, see the
costing note below), and **branch completion** (a branch has no stored achievement to set — a
branch's status is a *computed* state, derived from its descendant leaves at read time and never
written to a column, so it is complete exactly when they have all succeeded). A branch's **cost** is
computed the same way — never stored, always the read-time roll-up of its descendant leaves' costs
(see the costing section below) — so neither a branch's status nor its cost is authoritative stored
state; both are derived views over the leaf level.

## Requester intake: `department`, `request_holding_area`, `job_request`

Added by ADR 0033 (`docs/plans/2026-07-11-client-requester-intake-plan.md`) to support a low-privilege
`Requester` role that posts work into a configured holding area and monitors its own requests, without
gaining any operational job-browse, ownership, or work-recording authority.

| Column | Type | Notes |
|---|---|---|
| `department.id` | `bigint identity` | |
| `department.name` | `text` | Non-blank; unique among active departments. |
| `department.is_active` | `boolean` | |
| `department.row_version` | `bigint` | Optimistic concurrency token. |

| Column | Type | Notes |
|---|---|---|
| `app_user_department.app_user_id` | `bigint` | FK `app_user`. Composite PK with `department_id`. |
| `app_user_department.department_id` | `bigint` | FK `department`. |
| `app_user_department.is_primary` | `boolean`, nullable | At most one primary row per `app_user_id`. |

`request_holding_area` is a configured `JobNode` parent that accepts requester-created children — not
a new hierarchy type, deliberately: routing and eligibility metadata layered on the existing tree.

| Column | Type | Notes |
|---|---|---|
| `request_holding_area.id` | `bigint identity` | |
| `request_holding_area.job_node_id` | `bigint` | FK `job_node`. Parent under which requests are created; must not be `LeafWork`-bearing. |
| `request_holding_area.department_id` | `bigint`, nullable | FK `department`. `NULL` means globally eligible. |
| `request_holding_area.name` | `text` | Non-blank display name. |
| `request_holding_area.default_priority_id` | `smallint` | FK `priority`. |
| `request_holding_area.default_owner_user_id` | `bigint`, nullable | FK `app_user`. `NULL` places new requests in the unassigned pool (ownership model §2.1, ADR 0031). |
| `request_holding_area.is_active` | `boolean` | An inactive holding area rejects new submissions but does not affect existing `job_request` rows. |
| `request_holding_area.row_version` | `bigint` | Optimistic concurrency token. |

`job_request` anchors requester ownership/visibility to a specific `job_node` independently of
`job_node.owner_user_id` (technical ownership, unaffected) and independently of later moves or
decomposition — `PostedByUserId` alone is not sufficient, since not every posted node should behave
as an open requester ticket forever (ADR 0033).

| Column | Type | Notes |
|---|---|---|
| `job_request.job_node_id` | `bigint` | Primary key; FK `job_node`. One `job_request` per requester-originated node; the anchor node cannot be the permanent root. |
| `job_request.requester_user_id` | `bigint` | FK `app_user`. The submitting Requester; never inferred, set once at creation. |
| `job_request.holding_area_id` | `bigint` | FK `request_holding_area`. Records where the request entered the system; does not change when the node is later moved. |
| `job_request.requester_reference` | `text`, nullable | Optional requester-supplied tracking label. |
| `job_request.submitted_at` | `timestamptz` | |
| `job_request.closed_to_requester_at` | `timestamptz`, nullable | Set when staff close the request to further requester interaction. |
| `job_request.acknowledged_at` | `timestamptz`, nullable | Added by ADR 0034. Set once by staff (`AcknowledgeAsync`) — the public `Accepted` status is derived from this, not inferred from assignment or a move. |
| `job_request.acknowledged_by_user_id` | `bigint`, nullable | Added by ADR 0034. FK `app_user`. The staff actor who acknowledged the request. |
| `job_request.row_version` | `bigint` | Optimistic concurrency token. |

Moving or decomposing the anchor `job_node` (staff triage, `canMoveRequesterJob` in ADR 0033) updates
only `job_node.parent_id`; `job_request.holding_area_id` and `job_request.requester_user_id` are
untouched, and there is never a duplicate job left behind in the holding area. Requester progress and
the requester-safe read-only tree are computed from the anchor node's subtree, including descendants
created by later decomposition — not only from the anchor node's current direct leaf.

`job_request_note` (added by ADR 0034) is an append-only notes/comments thread rooted at the request's
anchor `job_node.id`, written by either staff or the requester through one authorization-branching
command (`AddNoteAsync`). A requester-authored note is always requester-visible; a staff-authored
note's visibility is caller-supplied. No update/delete path exists, mirroring `audit_event`'s
immutability.

| Column | Type | Notes |
|---|---|---|
| `job_request_note.id` | `bigint identity` | |
| `job_request_note.job_node_id` | `bigint` | FK `job_node`. The request's anchor node — notes stay attached there across decomposition. |
| `job_request_note.author_user_id` | `bigint` | FK `app_user`. Either the requester or a staff actor. |
| `job_request_note.content` | `text` | Non-blank. |
| `job_request_note.is_visible_to_requester` | `boolean` | `true` for every requester-authored note; caller-supplied for staff-authored notes. |
| `job_request_note.created_at` | `timestamptz` | |
| `job_request_note.row_version` | `bigint` | Optimistic concurrency token. |

## Rates and cost precedence

Labour cost is never stored — it's calculated dynamically from time worked and the rate resolved
for that time, at query time.

**Who may read a cost** is decided in two steps, both in `Domain.Authorization.CostAccessPolicy`.
`CanView` admits `Administrator`/`CostViewer`, or an owner of the queried node or one of its
ancestors (ADR 0040). `CanViewNodeCost` then filters each *individual* node inside that subtree
(ADR 0042): a branch's roll-up stays visible because it is an aggregate, as does the actor's own leaf
or an unassigned one, but another worker's individual leaf cost is withheld — that figure together
with the leaf's session hours would expose their hourly rate, which spec §7.3 reserves to the
rate/cost roles. A withheld cost is simply absent from the read model, never an error.

Two tables hold the inputs:

- **`user_cost_rate`** — an effective-dated hourly rate per user (`user_id`, `effective_start`,
  `effective_end` nullable, `rate numeric(19,6)`). Non-overlapping per user, enforced by a GiST
  `EXCLUDE` constraint over the effective-date range.
- **`node_rate_override`** — the same shape plus `node_id`, letting a specific job (or its
  descendants, via ancestor search) pay a different rate for a given user. Non-overlapping per
  `(node_id, user_id)` pair.

At any costed instant, the applicable rate is resolved in this order (spec §9.3):

1. an explicit rate on an effective **schedule exception** (`AddWorkingTime` with a rate override)
   covering that instant;
2. the **nearest** node-rate override for that worker — walking from the work's leaf up through
   ancestors, stopping at the first one with an override effective at that instant;
3. the worker's effective-dated `user_cost_rate`;
4. the worker's default hourly rate (`app_user.default_hourly_rate`).

Falling through all four is a costing error — it must never silently produce a zero cost. Each
resolved rate records which of the four it came from (`RateSource`: `OvertimeException`,
`NodeOverride`, `UserCostRate`, `UserDefault`, in that precedence order) — this is the "rate
provenance" shown in the cost report UI.

## The costing algorithm

Implemented in `src/JobTrack.Domain/Costing/` and `src/JobTrack.Domain/Rates/`, orchestrated by
`JobTrack.Application`'s `CostQueries`.

### Concurrent allocation

If a user has *N* simultaneously active work sessions (necessarily on different leaves — same-leaf
overlap is rejected at the database), each session gets an equal 1/N share of *time* for as long as
that concurrency level holds, not necessarily an equal share of *money* — each session's rate is
resolved independently, so two concurrent sessions on different jobs can have entirely different
hourly rates even though both belong to the same user at the same instant. There is deliberately no
fixed limit on N (the spec explicitly forbids an algorithm whose correctness depends on enumerating
pairs — 20+ simultaneous sessions must work correctly), and a session blocked by an unmet
prerequisite still contributes cost and still counts toward N — prerequisite state only affects
diagnostics, never cost allocation.

Because one user's concurrency level depends on *all* their active sessions, not just the ones under
the job being reported on, cost calculation fetches each worker's sessions **database-wide**, then
processes each worker's timeline independently:

1. **Partition** (`CostSegmentPartitioner`) — clip each session to the calculation's requested
   interval, then to the worker's effective working intervals; walk every resulting boundary
   (session start/end, every rate-override edge on the node or any ancestor, every user-rate edge,
   every schedule-exception edge) to produce maximal segments where the set of active sessions is
   constant. Each active session's share of a segment is kept as an **exact, unreduced fraction**
   `(segmentTicks, N)` — never rounded to whole ticks before use.
2. **Resolve** (`RateResolver`) — for each segment, resolve the applicable rate per the precedence
   order above.
3. **Calculate** (`SegmentCostCalculator`) — one session's monetary contribution for one
   constant-rate segment is a **single rounded division**, `rate × segmentTicks ÷ (N ×
   ticksPerHour)`, computed once directly to `decimal` — never `round(share) × rate`, which would
   silently reintroduce the rounding error the exact fraction exists to avoid (ADR 0009).
4. **Aggregate** (`HierarchicalCostAggregator`) — an explicit iterative (not recursive, so depth
   isn't bounded by the call stack) post-order walk up the hierarchy: a leaf's cost is the sum of
   its own sessions' contributions; a branch's cost is the sum of its descendant leaves'; the root's
   is the sum of everything.

Every worker's timeline is costed independently and the per-node results summed, since rates,
overrides, and concurrency are always resolved per worker.

### Exact cost, displayed cost, and penny reconciliation

`double`/`float` never appears anywhere on the duration or money path. Currency is carried at
`numeric(19,6)` internally — six decimal places of headroom above pennies for chained rate/time
multiplication — and rounded to pennies, midpoint-to-even, **only** at the point a value is actually
displayed or exported (ADR 0009):

- **`ExactCost`** — the full-precision, unrounded cost of a node and its subtree.
- **`DisplayedCost`** — `ExactCost` rounded to pennies for presentation.

Rounding each node's exact cost independently doesn't guarantee a displayed parent total equals the
sum of its displayed children's totals — independent rounding can be off by a penny either way. When
a report shows a hierarchy simultaneously (a parent alongside its children), `DisplayedCost` is not
enough; **hierarchy-display reconciliation** (ADR 0002) instead:

1. rounds every child's exact total to pennies independently (the naive value);
2. computes the residual: the parent's own (correctly rounded) total minus the sum of the naive
   child pennies;
3. if the residual is zero, keeps the naive values;
4. if not, assigns the entire residual to the single child with the largest rounding error, in the
   direction that cancels the residual (ties broken by a stable id order) — so displayed totals
   always add up exactly, one level at a time, leaf-to-branch and branch-to-branch up to the root.

This reconciliation is a presentation-layer step over already-computed exact costs — it never
changes what was actually earned or owed, only how a simultaneous multi-node display rounds.
