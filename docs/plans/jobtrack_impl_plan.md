# JobTrack Implementation Plan

**Status:** Proposed 4 (correctness fixes, EF-first data access, and functional-core / .NET-exception house style incorporated)  
**Date:** 2026-07-04  
**Normative specification:** `jobtrack_spec_codex.md`  
**Supplementary specification:** `jobtrack_spec_claude.md`  
**Target:** PostgreSQL-first database, conforming SQLite provider, .NET 10 reusable library, and ASP.NET Core .NET 10 web application

## 1. Purpose and authority

This plan turns the JobTrack specifications into an ordered, test-driven delivery programme. The delivery gates are strict:

1. database contracts and both provider implementations;
2. reusable library and provider conformance;
3. ASP.NET Core browser application and HTTP API; and
4. production hardening and release evidence.

> **Mandatory implementation order:** database first, reusable library second, ASP.NET Core front end third. This is an architectural constraint, not merely a suggested project schedule. Database contracts must be implemented and accepted before library feature implementation begins. The complete library boundary must then be implemented and accepted before ASP.NET feature implementation begins.

The phases shall not be developed as parallel feature tracks. Database work establishes the schema, invariants, transactions, canonical queries, versioned schema deployment scripts, and provider equivalence. Library work consumes those accepted contracts and establishes the provider-neutral domain and application API. Only then does the ASP.NET Core application consume the accepted library. A later phase may expose a defect in an earlier phase, but the correction must be made in the earlier phase and pass its gate again; it must not be bypassed or duplicated in a later layer.

`jobtrack_spec_codex.md` is authoritative. `jobtrack_spec_claude.md` may supply implementation detail only where it is consistent with the authoritative specification. A conflict is resolved in favour of the Codex specification and recorded in the implementation decision log before code is written.

In particular, the implementation shall not silently adopt secondary-spec differences as requirements. The following decisions have been reconciled explicitly:

- **Overtime rate (confirmed).** An additive schedule exception may carry an explicit hourly rate. That rate has highest precedence within the exception interval; an unpriced additive exception uses ordinary precedence. Overlapping explicitly priced additive exceptions for one user are prohibited.
- **Achievement (escalate / resolve).** Only canonical `Success` satisfies a prerequisite; an ordering threshold does not redefine success. The undefined legacy `RequiredAchievement` field has been removed. Phase 0 must still define the canonical achievement states, permitted transitions, reopening authority, and which non-success states are terminal.
- **Blocked sessions (confirmed, no conflict).** Recorded sessions remain costable and count in concurrency allocation after prerequisite regression. Both specifications already agree on this; the secondary spec's framing of it as an override of Codex is mistaken and no divergence exists.

SQLite is a complete, supported database backend for embedded or single-node deployments. It shall support the full JobTrack public contract, not a reduced feature set. It is not a PostgreSQL backup or disaster-recovery format. PostgreSQL backup, restore, point-in-time recovery, and failover are separate operational concerns.

## 2. Delivery principles

- Use test-driven development: add a failing test, implement the smallest correct behaviour, then refactor.
- Keep PostgreSQL authoritative for production semantics and performance while proving equivalent observable domain behaviour on SQLite from the database phase onward.
- Put domain behaviour, authorization of domain scope, transaction orchestration, auditing, and costing in the reusable library. Front ends do not access tables or reproduce rules.
- Use source-controlled, forward-only versioned schema deployment scripts. Application startup never creates or upgrades a production schema.
- This is a greenfield implementation. There is no legacy database, no operational data import, and no legacy data migration or transformation tooling.
- Treat public .NET APIs as compatibility commitments and review them against `Framework_Design_Guidelines_Essentials.md` before implementation and before each release.
- Use one captured clock value for each operation that depends on current time.
- Prefer simple constraints for row-local rules and transaction-safe database enforcement for cross-row invariants. Application pre-checks improve errors but do not replace concurrency-safe enforcement.
- Retain completed and cost-relevant history. Use archival rather than deletion.
- **Strongly favour EF Core for all data access.** Author every read and write in LINQ/EF first. Drop to hand-authored SQL only when EF genuinely cannot express or plan the operation correctly. When that happens on PostgreSQL, the irreducible logic is encapsulated as a source-controlled stored function or procedure (UDF, table-valued function, or `PROCEDURE`) and invoked *through* EF — mapped into LINQ with `HasDbFunction` where the function is composable, or executed with `FromSql`/`SqlQuery`/`ExecuteSql` otherwise — rather than as inline raw SQL strings scattered beside call sites. SQLite has no stored procedures, so its irreducible logic stays in the triggers already planned for enforcement plus minimal parameterized statements; application-side computation is preferred over inline SQL where EF cannot help.
- Run every automated test command through `gtimeout`.

## 3. Proposed repository and solution layout

Create one .NET 10 solution with dependency direction enforced by project references and architecture tests:

```text
JobTrack.slnx
Directory.Build.props
Directory.Packages.props
global.json
database/
  postgresql/
    schema-versions/
    reference-data/
    verification/
  sqlite/
    schema-versions/
    reference-data/
    verification/
  scenarios/
src/
  JobTrack.Abstractions/
  JobTrack.Domain/
  JobTrack.Application/
  JobTrack.Persistence.PostgreSql/
  JobTrack.Persistence.Sqlite/
  JobTrack.Database/
  JobTrack.Identity/
  JobTrack.Web/
  JobTrack.AdminCli/
tests/
  JobTrack.Domain.Tests/
  JobTrack.Application.Tests/
  JobTrack.Database.ContractTests/
  JobTrack.Persistence.PostgreSql.Tests/
  JobTrack.Persistence.Sqlite.Tests/
  JobTrack.PublicApi.Tests/
  JobTrack.Identity.Tests/
  JobTrack.Web.IntegrationTests/
  JobTrack.Web.EndToEndTests/
  JobTrack.ArchitectureTests/
  JobTrack.TestSupport/
docs/
  decisions/
  operations/
  threat-model/
```

This layout deliberately diverges from both specifications' illustrative project lists and shall be recorded as an ADR: it keeps the primary spec's split persistence providers (`Persistence.PostgreSql` / `Persistence.Sqlite`) rather than the secondary spec's single `JobTrack.Data`, adopts the secondary spec's EF-first stack across both providers (§5.1 item 7), introduces `JobTrack.Identity` (in neither spec's list) as a front-end-phase adapter, and places the cross-provider EF model configuration in one internal shared component (§7.4) so the two providers cannot drift.

`JobTrack.Abstractions`, `JobTrack.Domain`, `JobTrack.Application`, and both persistence providers form the reusable library and have no ASP.NET Core dependency. `JobTrack.Application` depends on the abstractions and domain projects, persistence providers implement internal application ports, and hosts compose the resulting client. `JobTrack.Identity` is a front-end-phase adapter around ASP.NET Core Identity for authentication, credential administration, and security-stamp operations; it is not part of the reusable JobTrack library. Neither `JobTrack.Web`, `JobTrack.Identity`, nor `JobTrack.AdminCli` contains domain SQL.

Pin the .NET SDK and centrally manage package versions. Enable nullable reference types, implicit usings, deterministic builds, warnings as errors, analyzers, source-link/package metadata, and a locked restore in CI. At implementation start, select and pin the latest stable PostgreSQL major version; only that major version is supported for the initial release. Record exact PostgreSQL, SQLite, .NET SDK, and package versions rather than allowing builds to drift as “latest” changes.

## 4. Cross-cutting definition of done

Each vertical behaviour is complete only when:

- its acceptance case was first expressed as a failing test;
- PostgreSQL and SQLite return the same public result or stable error category where the common contract applies;
- concurrency and rollback behaviour are tested when the invariant spans rows or tables;
- public APIs have XML documentation, nullability annotations, cancellation behaviour, and documented exceptions;
- authorization is tested at both the library scope boundary and direct HTTP boundary where applicable;
- audit output is asserted and contains no secret material;
- telemetry is structured, bounded, and contains no rates, credentials, reset data, or unrestricted personal data;
- schema deployment works from empty state and every supported prior schema version;
- formatting, analyzers, unit tests, integration tests, and dependency/security scans pass under bounded timeouts; and
- the relevant specification requirement is linked from the test or implementation decision record.

## 5. Phase 0 — foundation and executable requirements

### 5.1 Decisions to freeze

Create short architecture decision records for:

1. specification precedence and the known secondary-spec differences;
2. the atomic first-administrator, permanent-root, and initialised-marker bootstrap using protected interactive secret input and no default credential. **Sequencing resolution:** the bootstrap is a single-transaction *Application-library* command (M5) that writes the `app_user` profile, the Identity credential row (username, password hash, security stamp, force-change flag), the permanent root, and the initialised marker together. It depends only on the `IPasswordHasher<T>` abstraction from `Microsoft.Extensions.Identity.Core` (which carries **no** ASP.NET Core dependency) plus a narrow library-owned credential-write port against the Identity tables established in the database phase (§6.2.2). It does **not** depend on the front-end-phase `JobTrack.Identity` adapter, so the atomic transaction is buildable and fully testable in the library phase; the adapter later supplies only the runtime `UserManager`/store wiring for ordinary authentication, not the bootstrap credential write;
3. identifiers (`bigint` internally and strongly typed identifiers publicly unless profiling or distribution requirements justify UUIDs);
4. canonical SQLite instant encoding and precision;
5. DST gap and repeated-time resolution policy, including recording the TZDB version used by the deployed application;
6. decimal precision and midpoint-to-even GBP presentation rounding, plus the allocation-precision policy: segment duration is stored in integer ticks and each active session's equal share is the exact rational `segmentTicks / N`; it is never rounded back to whole ticks. **The rational share is not converted to a rounded `decimal` before summation** — doing so breaks exact time-conservation whenever `N` has a factor of 3, 7, etc. (`1/3` hour truncated then tripled ≠ the segment duration). Instead: (a) allocated *time* is carried as the exact rational `(segmentTicks, N)` (or an equivalent high-precision value used only for display/diagnostics), and the allocation-conservation property test asserts *exact* equality on that rational, not a tolerance; and (b) the monetary contribution for a session in a constant-rate segment is computed as a **single** rounded division `rate × segmentTicks ÷ (N × ticksPerHour)`, never as `round(share) × rate`. Currency is rounded to pennies only at the accepted reporting boundary. The ADR must also decide whether displayed hierarchy levels reconcile exactly and, if required, define deterministic penny reconciliation. Binary floating point (`double`) shall not appear anywhere on the duration or money path, including cost-result contracts, so `CostLine.AllocatedHours`-style fields are `decimal` (or the rational value object), never `double`;
7. EF Core 10 as the single, strongly preferred data-access technology. **All reads and writes are authored in LINQ/EF first** — ordinary persistence mapping, command change tracking, LINQ read models, application-managed `bigint` concurrency properties, and value conversion. Hand-authored SQL is a last resort, permitted only where EF genuinely cannot express or plan the operation correctly: recursive-CTE hierarchy/prerequisite graph queries, range/exclusion operations, deferred-constraint workflows, advisory locks, database-wide overlap discovery, and canonical cost-input queries. **Where irreducible SQL is required on PostgreSQL it is encapsulated as a source-controlled stored function or procedure (UDF / table-valued function / `PROCEDURE`) and invoked through EF** — composable functions are mapped into LINQ with `modelBuilder.HasDbFunction(...)` so they participate in query composition and plan review; non-composable logic is called with `FromSql`/`SqlQuery`/`ExecuteSql`. Inline raw-SQL string literals scattered beside call sites are not the pattern; the SQL lives in the database as a named, versioned object with a stable logical identifier for error translation. SQLite, having no stored procedures, keeps its irreducible logic in the enforcement triggers plus minimal parameterized statements, with application-side computation preferred where EF cannot help; the two providers must still return the same public result or stable error category. The decision to prefer EF over a thin mapper is made deliberately here: EF earns its place through change tracking, the Identity store, application-managed concurrency, and value conversion, and the encapsulate-as-function rule keeps the residual SQL reviewable rather than sprawling. Compiled queries, interceptors, and owned types are adopted only where profiling or a demonstrated cross-cutting requirement justifies them. Production ASP.NET Core Identity integration is isolated in the front-end-phase `JobTrack.Identity` adapter (the library-phase bootstrap credential write of item 2 uses only `Microsoft.Extensions.Identity.Core`). EF Core *migrations* remain excluded as the production schema mechanism because the authoritative schema uses PostgreSQL features EF's migration model cannot represent and requires forward-only, source-controlled, checksum-verified SQL applied by an explicit deployer;
8. schema deployment-script format, schema versioning, and supported upgrade window;
9. PostgreSQL lock keys and structural-operation serialization;
10. internal public API compatibility and versioning policy;
11. single-server deployment, secret source, and PostgreSQL recovery objectives;
12. canonical achievement states, permitted transitions, completed-state semantics, and audited reopening authority;
13. the provider-specific implementation of the authoritative bootstrap state machine and permanent-root guard — a partial unique index gives only *at most* one root, so the initialised marker, root creation, and irreversible arming operation must be atomic;
14. the internal time library. **Decision: Noda Time inside the domain** (`Instant`, IANA `DateTimeZone`, `LocalDateTime`/`LocalTime` civil-time schedules, and explicit `ZoneLocalMapping` skipped/ambiguous resolvers), with `DateTimeOffset` kept at the public boundary and Noda converted at the edges. Every result records the bundled TZDB version used. Reproduction is defined by persisted state, `asOf`, and TZDB version; a TZDB upgrade may deliberately change historical recalculation and must be disclosed and tested. Retaining old TZDB bundles is required only if the operational reproducibility policy requires recalculation under an older version. The daylight-saving spike (§5.3) confirms the resolver behaviour before schema and API contracts are frozen; and
15. historical schedule, exception, user-rate, and node-override correction semantics: whether past rows may be replaced, split, or retired; the required reason and audit before/after evidence; and the atomic overlap revalidation rules;
16. **costing read scope versus subtree authorization.** Correct concurrency requires discovering *all* of a worker's overlapping sessions across the whole database (§10.2.2 of the specification), including sessions on jobs the caller is not authorized to browse. The decision: the cost engine runs with an internal elevated read scope for the sole purpose of computing `N`; only aggregate allocation for the *requested* jobs is ever exposed, and the foreign sessions, their nodes, and their rates are never surfaced in any result, breakdown line, or diagnostic. A test asserts that a caller scoped to node X receives an allocation influenced by the worker's out-of-scope concurrent work yet can never read those foreign sessions. The residual information exposure — that a requested-job cost lower than the naïve single-session figure implies the worker was concurrently busy elsewhere — is accepted and documented; and
17. **cost-engine behaviour on an invalid same-(user, LeafWork) overlap.** The specification both requires costing to include sessions created by corruption or raw writes (they are evidence of recorded labour) and prohibits same-user/same-leaf overlap. If such an overlap nonetheless reaches the engine (raw write, or an out-of-band SQLite state), the engine shall not silently allocate it: it throws an `InvariantViolationException` (stable `ConstraintId`) identifying the offending sessions rather than double-counting the same leaf's time. A negative/golden test covers this path;
18. **house style: modern idiomatic C# with a functional core, using .NET exceptions as the sole failure channel.** The domain and application layers are written functional-first: immutable data by default (`record` / `readonly record struct` for value objects, `init`-only or `required` members, no mutable public state), pure functions for domain logic (the cost engine is the exemplar), and no mutable persistence entity graphs escaping into the domain. Use current language features where they improve clarity — primary constructors, file-scoped namespaces, `switch` expressions and pattern matching, collection expressions, target-typed `new`, `nameof`. Matching over closed sets (`Achievement`, `RateSource`, schedule-exception effect) uses **exhaustive** `switch` expressions with no silent `default` fallthrough, so adding a case is a compile-time obligation. **The one deliberate, mandated departure from functional purity is error handling: follow .NET exception idioms 100%, everywhere — never return error codes and never use a `Result`/`Either`-style error channel, internally or at the boundary.** Every failure throws: framework exceptions for caller/usage errors (`ArgumentException`, `ArgumentNullException`, `ArgumentOutOfRangeException`, `InvalidOperationException`, `OperationCanceledException`), and the shallow `JobTrackException` hierarchy (§7.1, §12.6 of the spec) for conditions callers handle distinctly. This is *not* a contradiction of functional style but the single sanctioned exception to it. **Throwing is the default; the `Try*` pattern is the sanctioned relief valve, exactly as FDG frames it** — a *performance* accommodation (`int.TryParse` is the canonical example) for an operation that would otherwise throw *often enough in normal use* that the throw cost is a measured problem, or for genuine expected absence. A `Try*` member returns `bool` with an `out` result, **complements** rather than replaces the throwing member where both make sense, and is **not** an error-code channel: it signals only success/failure plus the value, never a failure *category*. A caller who must distinguish *why* something failed uses the throwing member and catches the typed exception; the `Try*` variant exists only to avoid the throw on a genuinely hot path. Introducing a `Try*` member is therefore an evidence-driven decision (a real hot path or a common-failure scenario), not a default shape for every query. The inherited implementation house style (Allman braces, four-space indent, explicit visibility, one public type per file) still applies; field-prefix conventions matter less as mutable fields are minimised.

The achievement workflow, hierarchy-display rounding reconciliation, and historical schedule/rate correction policies (items 12, 6, 15) were product-semantic and required confirmation with the product owner rather than resolution by document precedence alone. **Each of these three shapes columns or constraints — terminal-state metadata, reconciliation storage, and correction/audit rows respectively — so all three were hard M0 exit blockers (§5.5).** They are now closed and recorded as ADRs:

- **Achievement states and reopening (item 12) — closed, see `docs/decisions/0001-achievement-states.md`.** `Achievement` is `Waiting | InProgress | Success | Cancelled | Unsuccessful`. Only `Success` satisfies a prerequisite. Any terminal state may be reopened to `Waiting` by a Job manager or Administrator with a mandatory reason and audit record; no second-person approval.
- **Hierarchy-display penny reconciliation (item 6, reconciliation half) — closed, see `docs/decisions/0002-penny-reconciliation.md`.** Displayed parent totals must exactly equal the sum of displayed child totals at every level. Each child is rounded to the nearest penny (midpoint-to-even); any residual against the exact parent total is applied entirely to the single child with the largest rounding error, with a stable tie-break, applied one hierarchy level at a time. This is a display-boundary computation only and never mutates the underlying exact rational/decimal values.
- **Historical schedule/rate correction semantics (item 15) — closed, see `docs/decisions/0003-historical-correction.md`.** Historical schedule versions, exceptions, user rates, and node overrides may be corrected in place (replace, split, or retire) by the appropriate role, even after sessions have been costed against them, subject to a mandatory reason, full before/after audit, in-transaction re-validation of the same overlap/non-overlap constraints as ordinary inserts, and optimistic-concurrency checks. There is no locked cutoff and no "final" cost snapshot; recalculated costs simply reflect corrected history.

**All eighteen §5.1 decisions are now closed.** Items 12, 6 (reconciliation half), and 15 are the product-semantic ADRs above; the remaining items (1–5, 6 precision half, 7–11, 13–14, 16–18) are closed as ADRs 0004–0019 in `docs/decisions/` — see that directory for the full index. `docs/plans/jobtrack_impl_plan.md`'s own text above remains the narrative source for each decision; the ADRs are the reviewable, individually citable record of each one's final form and consequences.

Amend the authoritative specification first if product behaviour must change; do not infer unresolved product semantics from the secondary specification. The illustrative C# surface and DDL in the secondary specification's appendices (e.g. `double AllocatedHours`, the speculative `Achievement { …, NotAchieved, Partial, Success }` enum) are **non-normative**; the reviewed API baseline and schema are authored fresh against these decisions, not seeded from those sketches.

### 5.2 Test catalogue and traceability

**Skeleton established — see `docs/traceability/test-catalogue.md`.** It defines the stable test-case identifier scheme, the test-category timeout budgets, the traceability table (extended as each schema/application slice is decomposed into concrete tests), and the golden-scenario catalogue below.

Convert every acceptance criterion and invariant into stable test-case identifiers. Maintain a traceability table mapping:

`specification clause -> database test -> domain/application test -> HTTP/end-to-end test -> operational evidence`.

Build deterministic golden scenarios for:

- minimal, deep, and broad hierarchies;
- leaf work with no sessions and with multiple sessions;
- prerequisite chains, blocked work, and forbidden cycles;
- schedules in multiple IANA zones, including DST gaps and repeated times;
- additive and subtractive schedule exceptions, including priced and unpriced overtime;
- user rates, inherited node overrides, gaps, and boundary changes;
- concurrent sessions at `N = 1`, `2`, `20`, and `100+`;
- two or more of one user's concurrent sessions falling inside a single explicitly priced additive (overtime) exception — since eligibility is per-user, every overlapping session receives the rank-1 overtime rate *and* a `1/N` share; the scenario pins this deliberately so the surprising-but-correct result is a decision, not an accident;
- an open session whose prerequisite later regresses; and
- exact allocation, rate provenance, and GBP results at a fixed `asOf`.

### 5.3 De-risking spikes before schema freeze

Prove the genuinely novel or risky persistence mechanisms with focused spikes *before* the baseline schema is committed — the first implementation increment (§13) establishes the TDD pattern on the simplest slice, but the mechanisms most likely to be wrong must be de-risked first. Every spike that concerns an invariant is validated with **simultaneous independent connections**; a passing single-threaded proof is insufficient. Spike, at minimum:

- deferred constraint triggers enforcing leaf/branch exclusivity and the single-root invariant under concurrent structural writes;
- prerequisite-cycle and hierarchy-ancestor/descendant checks under concurrent edge insertion;
- GiST exclusion constraints for same-user/same-leaf session overlap and for rate/schedule effective ranges, including the unbounded-upper `tstzrange` for open sessions;
- transaction-scoped advisory-lock serialization for subtree moves and decomposition, with deterministic lock ordering;
- timezone conversion across DST gaps and folds, exercising Noda Time's `ZoneLocalMapping` skipped/ambiguous resolvers against a pinned TZDB version (confirms the §5.1 item 14 decision and the item 5 DST policy); and
- the segment-based concurrent cost sweep at `N = 2`, `20`, and `100+`, checked against the independent oracle of §7.2.

Spike code is throwaway proof, not production feature work, and does not pass through the delivery gates; its purpose is to retire risk and inform the frozen decisions.

**Complete — see `docs/traceability/spike-report.md` and `spikes/`.** All six bullets above were run against a real local PostgreSQL instance with simultaneous independent connections (never single-threaded) and against pinned TZDB data. Two concrete findings came out of the spikes and are now folded back into the frozen decisions: ADR 0012 gained a proven "prerequisite-graph writes" lock domain (deferred constraints alone let two concurrent edges jointly create a cycle), and the GiST session-overlap exclusion constraint's concurrent-conflict path was found to surface as a PostgreSQL deadlock (`40P01`) rather than a clean exclusion violation (`23P01`), which §7.4's error-translation layer must account for.

### 5.4 Performance and scale budgets

**Defined — see `docs/traceability/performance-budgets.md`.** It records the representative dataset scales (deep tree, broad tree, combined production tree, long history, many users, high concurrency), the per-operation P95 latency and query-plan budgets measured against them, and the write-contention budgets for the race scenarios in §6.6.

Define measurable dataset scales and query-plan/latency budgets **now**, as a Phase 0 deliverable, for hierarchy navigation, database-wide overlap discovery, prerequisite/readiness checks, and cost calculation — so the database gate (§6.7) tests against agreed targets rather than retrofitting them. Record the representative scales (deep tree, broad tree, long history, many users, high concurrency) alongside the budgets.

### 5.5 Foundation exit criteria

- The solution builds from a clean checkout with the pinned SDK.
- Test categories and timeout budgets are documented.
- The requirement traceability skeleton exists.
- The de-risking spikes (§5.3) demonstrate the required PostgreSQL behaviour under concurrent writes, and the time model handles the documented DST cases deterministically.
- Performance and scale budgets (§5.4) are defined and recorded.
- All semantic and technology decisions needed for database design are accepted.
- The three product-semantic decisions (§5.1 items 12, 6, 15 — achievement workflow, hierarchy-display penny reconciliation, historical schedule/rate correction) are closed with the product owner. These are on the schema-freeze critical path and block M0 exit. **Closed — see ADRs 0001–0003 in `docs/decisions/`.**
- M0 is formally accepted before any M1 schema version is frozen or production implementation begins. **Accepted — see ADR 0020 (`docs/decisions/0020-m0-acceptance.md`).** M1 schema-version work (§6.2) may now begin.

## 6. Phase 1 — database contracts

Database work proceeds in small TDD slices. For every slice, first add shared contract tests, then PostgreSQL enforcement, then SQLite enforcement, then provider-specific concurrency tests.

### 6.1 Schema deployment substrate

Implement an explicit schema deployment tool that:

- validates provider and current schema version;
- acquires a provider-appropriate deployment lock;
- applies ordered forward-only schema versions transactionally where supported;
- separates schema/reference data from development scenarios;
- records schema-version identifier, checksum, application version, actor, and timestamp;
- refuses changed checksums or unknown/newer schema versions; and
- supports dry-run/validation without granting the web application DDL rights.

PostgreSQL deployment scripts establish separate owner, schema-deployer, application, read-only/reporting, and emergency-reset roles. SQLite deployment scripts enable and verify foreign keys for every connection. Production startup performs a schema compatibility check only and fails closed on incompatibility.

### 6.2 Schema slice order

Implement in this dependency order:

1. schema-version metadata and stable reference tables;
2. `app_user`, ASP.NET Core Identity storage, and their one-to-one account link;
3. the explicit atomic first-administrator, permanent-root, and initialised-marker bootstrap operation;
4. `job_node`, creation of the permanent root by that administrator, ownership, versioning, archival, and an explicit guard making the root undeletable and un-re-parentable with a defined arming point relative to bootstrap (minimum cardinality cannot be enforced by row triggers on an empty table);
5. hierarchy acyclicity, reachability, atomic move semantics, and revalidation of every prerequisite edge affected by a move (a move can newly violate the ancestor/descendant prohibition even while the tree stays acyclic);
6. `leaf_work` and leaf/branch/root exclusivity;
7. `work_session`, interval ordering, active-session uniqueness, and same-user/same-leaf non-overlap;
8. `job_prerequisite`, DAG enforcement, hierarchy-edge exclusion, and readiness queries;
9. effective-dated schedule versions and weekly intervals;
10. additive/subtractive schedule exceptions, optional additive-exception rates, and non-overlap of explicitly priced additive exceptions;
11. effective-dated user rates and inherited node overrides;
12. append-only `audit_event` storage and access restrictions; and
13. canonical hierarchy, achievement, eligibility, overlap-candidate, and cost-input queries.

Before freezing these schema versions, add a throwaway database-phase compatibility spike which uses ASP.NET Core Identity to create, read, update, and revoke accounts on PostgreSQL and SQLite. This proves that the reviewed Identity schema, normalized-name uniqueness, security-stamp behaviour, and provider mappings work on both backends. The spike does not become production library code: production Identity integration is implemented only in the front-end phase after the library gate.

Use named constraints and stable logical constraint identifiers. Error translation must depend on PostgreSQL SQLSTATE plus constraint name or a SQLite trigger/result code, never free-form message text.

### 6.3 PostgreSQL reference design

Use native PostgreSQL features where they strengthen integrity:

- `timestamptz` for instants, `date`/civil-time values for recurring schedules, and `numeric(19,6)` for rates and precise monetary inputs;
- foreign keys with `ON DELETE RESTRICT` for historical and cost-relevant relationships;
- deferred constraint triggers and recursive CTEs for root, hierarchy, leaf/branch, and prerequisite graph invariants;
- GiST exclusion constraints for non-overlapping user rates, node overrides, schedule versions, and same-user/same-leaf sessions;
- an unbounded-upper `tstzrange` for unfinished sessions and a partial unique index for one active session per user/leaf;
- a user-leading GiST overlap index plus measured B-tree/partial indexes for database-wide concurrency discovery;
- explicit application-managed `bigint` versions; and
- `REPEATABLE READ` for cost-input snapshots.

Structural commands take transaction-scoped advisory locks only where concurrent tests show that constraint deferral alone is insufficient or produces unacceptable contention. Lock acquisition order must be deterministic and documented.

### 6.4 SQLite conformance design

Use one documented integer UTC epoch encoding at a fixed precision. On every connection, configure and verify foreign keys, busy timeout, journal mode, and synchronous policy appropriate to deployment.

Use `CHECK`, `UNIQUE`, and foreign keys for local rules; triggers plus recursive CTEs for overlap and graph guards; and immediate write transactions for structural operations. Where SQLite cannot defer an invariant, use a validated replacement operation with carefully ordered writes inside the same transaction. Never temporarily disable foreign keys or production triggers.

Document SQLite's single-writer operational envelope. Load/concurrency tests may have different performance expectations, but a successful operation must have the same domain effect and stable public error category as PostgreSQL.

### 6.5 Canonical query contracts

Define typed result contracts and golden results for:

- subtree and ancestry traversal;
- recursively derived achievement;
- unsatisfied prerequisite explanations;
- worker-scoped, database-wide overlap discovery using strict half-open predicates;
- historical schedule selection and normalized effective working intervals;
- overtime-exception rate resolution, then nearest-ancestor node-rate, user-rate, and default-rate fallback, over an exhaustive boundary set: the partition must add boundaries for session start/end, schedule-interval edges, exception edges, `user_cost_rate` edges, and `node_rate_override` edges for every ancestor of each session's node that holds an override for that worker (the secondary spec's Appendix C.4 sketch omits node-override ancestor-chain boundaries and is incorrect on this point);
- complete immutable inputs for the cost engine; and
- append-only audit search with permission-sensitive projections.

On PostgreSQL each canonical query that EF cannot express or plan correctly — recursively derived achievement, readiness/unsatisfied-prerequisite explanation, worker-scoped database-wide overlap discovery, normalized effective working intervals, and the rate-resolution/boundary helpers (`resolve_rate`, `clip_to_working_set`, `user_rate_boundaries`, `node_succeeded`, readiness) — is authored as a source-controlled stored function or table-valued function with a stable logical identifier, deployed by the migration scripts, and invoked from EF (`HasDbFunction` for composable functions, `FromSql`/`SqlQuery` otherwise). The canonical query *contract* is therefore the function signature plus its typed result, not an inline SQL string. SQLite provides the equivalent behaviour through its enforcement triggers and minimal parameterized statements, returning identical typed results.

Query plans are part of PostgreSQL production evidence, not the public API. Test representative scale data for deep/broad trees, long temporal histories, many users, and high concurrency. Assert plan shape and latency budgets without brittle exact-cost assertions. Inspect SQLite query plans for the user-leading temporal indexes.

### 6.6 Database test strategy

The shared database contract suite runs against disposable real PostgreSQL and file-backed SQLite databases. It tests successful effects, failed writes, transaction rollback, and stable error identities. Raw SQL helpers exist only in test support to prove constraints reject bypass attempts.

The test-support library is itself tested — deterministic identity and clock control, transaction cleanup, migration isolation between fixtures, safe parallel execution, and prevention of accidental production references — so fixture flakiness cannot masquerade as a product defect.

Add schema-introspection tests for columns, types, nullability, keys, foreign keys, checks, exclusion constraints, indexes, triggers, functions, roles, and grants. These tests detect accidental schema drift that behavioural fixtures may not expose.

Every trigger must name one narrowly stated invariant, handle both insert and update paths where applicable, produce a stable translatable error identity, and have tests for valid multi-step transactions, invalid direct writes, rollback, and concurrent races. Review triggers as synchronization code rather than ordinary row validation.

Add race tests for:

- creation of a second root;
- deletion or re-parenting of the sole root;
- opposing moves that would create a cycle;
- a move that makes a node an ancestor or descendant of its prerequisite counterpart;
- adding a child while leaf work is attached;
- concurrent same-user/same-leaf sessions;
- concurrent prerequisite edges that jointly create a cycle;
- overlapping rate and schedule ranges;
- overlapping explicitly priced additive exceptions for the same user; and
- stale optimistic-concurrency versions.

Use generated reproducible data with recorded seeds. Preserve failing seeds as regression fixtures.

### 6.7 Database gate

The database gate passes only when:

- empty and upgrade-path schema deployments pass on both providers;
- every invariant has a named enforcement mechanism and a passing bypass test;
- shared query fixtures produce equivalent results;
- race tests demonstrate integrity after commit and rollback;
- PostgreSQL query plans and scale budgets meet the agreed production targets;
- SQLite limitations and configuration requirements are documented;
- role grants prove the normal application role cannot perform DDL, erase audit rows, or delete retained history; and
- a schema-level PostgreSQL backup/restore smoke test passes, and SQLite deployment backup procedures are documented; production-like recovery-objective rehearsal belongs to the release gate.

No library feature implementation begins before this gate passes. ASP.NET feature implementation is also prohibited at this stage, apart from narrow technical spikes explicitly required to validate a database contract, such as Identity schema compatibility; spike code is not production feature implementation.

## 7. Phase 2 — reusable .NET library

### 7.1 Public API design before implementation

Write consumer-first API specifications and compiling usage examples before creating implementations. Review the proposed surface against the Framework Design Guidelines:

- one configured entry point, `IJobTrackClient`, exposing cohesive job, work, schedule, rate, audit, and costing capabilities;
- immutable request/result records and read-only collection contracts;
- strongly typed identifiers and small immutable value types only where they prevent primitive confusion;
- task-based asynchronous methods with `Async` suffix and final optional `CancellationToken`;
- methods, not properties, for I/O or calculations;
- explicit nullable or `Try` contracts for expected absence;
- no provider, SQL, ASP.NET Core, mutable entity, or connection types in public APIs;
- no Boolean parameter clusters where an enum expresses intent;
- optimistic-concurrency versions in mutation requests and results;
- immutable-first, functional-core implementation (§5.1 item 18): `record`/`readonly record struct` contracts, pure domain functions, exhaustive `switch` expressions over closed enums, and modern language idioms (primary constructors, file-scoped namespaces, pattern matching, collection expressions) — with .NET exception idioms followed 100% as the sole failure channel: every failure throws (framework types for usage errors, the `JobTrackException` hierarchy otherwise), no error codes and no `Result`-style error channel internally or at the boundary; throwing is the default and the `Try*` pattern is used only as FDG's performance accommodation (the `TryParse` rationale — a measured hot path or common-failure scenario) complementing a throwing member, or for genuine expected absence, never to carry a failure category back to the caller; and
- additive evolution after 1.0, with API compatibility baselines checked in CI.

Use framework exceptions for invalid caller arguments and a shallow documented `JobTrackException` hierarchy for failures callers handle differently: not found, authorization denied, concurrency conflict, prerequisite blocked, missing rate, and invariant violation. Validate public arguments synchronously. Provider exceptions never cross the facade.

### 7.2 Domain implementation order

Implement pure domain behaviour in this order, with unit and property tests first:

1. identifiers, instants, half-open intervals, rates, money, and validation;
2. interval clipping, union, subtraction, normalization, and intersection;
3. hierarchy classification and recursive achievement;
4. prerequisite readiness across the leaf and all its ancestors, with diagnostic explanations identifying inherited blockers;
5. effective-dated schedule expansion with explicit DST resolution;
6. exception union/subtraction precedence;
7. rate timelines and nearest-ancestor precedence;
8. cost boundary partitioning over the exhaustive boundary set of §6.5 (including node-override ancestor-chain boundaries) and arbitrary `1/N` allocation represented exactly as `segmentTicks / N`, without assigning residual ticks to individual sessions; and
9. hierarchical aggregation using exact rational duration shares carried as `(segmentTicks, N)` and `decimal` monetary values computed per constant-rate segment as the single rounded division `rate × segmentTicks ÷ (N × ticksPerHour)` (never `round(share) × rate`), with midpoint-to-even GBP rounding only at the reporting boundary and the accepted hierarchy-reconciliation policy — no `double` anywhere on the duration or money path (§5.1 item 6).

The cost engine is deterministic and side-effect-free over immutable, fully materialized input plus one `asOf`. It computes `N` from the worker's complete database-wide overlapping-session set (discovered under an internal elevated read scope, §5.1 item 16) but retains and exposes only requested-job amounts. If its input contains an invalid same-(user, LeafWork) overlap (§5.1 item 17), it throws `InvariantViolationException` rather than double-counting. Property tests prove allocation conservation as *exact* rational equality (allocated time across a segment's sessions equals the segment duration, no tolerance), ordering independence, no double counting, and boundary stability. In addition, an **independent overlap oracle** — a deliberately naive reference implementation that samples cost per instant (or brute-forces pairwise membership) rather than sweeping boundaries — cross-checks the boundary-partition engine on golden and generated datasets. The oracle is too slow for production but is a second, structurally different witness that catches segmentation and rate-resolution errors the properties alone can miss. Differential tests compare PostgreSQL and SQLite inputs and, if later added, any PostgreSQL-optimized cost query against this canonical engine.

Define one canonical cost-segment trace used by golden tests, diagnostics, and authorized cost-detail views. Each segment records its half-open interval, working-time eligibility, active-session membership, concurrency divisor, resolved rate and provenance, allocated duration, and unrounded contribution. This makes every reported amount explainable without persisting calculated totals as authoritative data.

### 7.3 Application command/query slices

For each slice, write application tests with fake ports, then provider conformance tests using real databases:

1. atomically bootstrap the first administrator, permanent root, and initialised installation marker through one explicit administrative command in a single transaction, hashing the credential via the injected `IPasswordHasher<T>` (`Microsoft.Extensions.Identity.Core`, no ASP.NET Core dependency) and writing the credential row through the library-owned credential-write port, so the command is fully testable in this phase without the front-end `JobTrack.Identity` adapter (§5.1 item 2);
2. employee profile and account-state queries;
3. create, edit, move, archive, and conditionally delete planning nodes;
4. attach leaf work and decompose a worked leaf atomically — the highest-risk structural operation (it re-parents sessions across the same-user/same-leaf overlap exclusion constraint under deferred constraints and advisory locks) and the priority target for concurrency and rollback testing;
5. add/remove prerequisites and query readiness;
6. start, finish, resume, and correct work sessions, with pause and stop treated as UI terms for finishing the active session;
7. change achievement subject to prerequisite gates;
8. add schedule versions and exceptions;
9. add user rates and node overrides;
10. calculate cost details and hierarchy totals, discovering the workers' database-wide overlapping sessions under an internal elevated read scope for `N` while exposing only requested-job amounts and never the foreign sessions/nodes/rates (§5.1 item 16); and
11. query audit history using sensitive-field projections.

Every command accepts an actor and correlation context, loads authoritative roles/account state/ownership, authorizes inside the transaction, rechecks mutable gates, uses compare-and-swap versions, emits audit intent, and commits once. Finishing an existing session remains possible after prerequisite regression; starting or completing work does not.

### 7.4 Persistence implementations

Use EF Core 10 as the single general-purpose library persistence mechanism, with `Npgsql.EntityFrameworkCore.PostgreSQL` for PostgreSQL and `Microsoft.EntityFrameworkCore.Sqlite` for SQLite. EF Core owns domain persistence mapping, change tracking for commands, ordinary LINQ read models, transaction participation, and application-managed optimistic-concurrency tokens configured as EF concurrency properties. It does not own authentication or ongoing credential persistence; those belong to the later `JobTrack.Identity` adapter. The single exception is the one-time bootstrap credential row (§5.1 item 2, §7.3 step 1): the library writes it through a narrow credential-write port using only `Microsoft.Extensions.Identity.Core`'s `IPasswordHasher<T>`, against the Identity tables created in the database phase — it does not host the runtime Identity `DbContext`, stores, or `UserManager`, which remain in `JobTrack.Identity`. Use value converters where they preserve domain types without obscuring SQL representation. Adopt owned types, compiled queries, or interceptors only for a demonstrated mapping, performance, audit, or telemetry requirement. EF Core does not own the database design, schema deployment, cross-table invariants, or domain model.

Keep `DbContext`, entity configurations, persistence entities, and provider extensions internal to the persistence assemblies. Do not expose tracked entities through application or public contracts. Disable lazy loading; use explicit projections and `AsNoTracking` for read models by default; and use tracking only for intentional command updates. Avoid generic repositories that merely obscure `DbContext`; application ports should represent JobTrack use cases or cohesive persistence capabilities.

PostgreSQL uses one configured pooled `NpgsqlDataSource` shared with its EF Core configuration. SQLite uses an explicitly configured connection factory and immediate transactions where writes require serialization. One logical mutation uses one context/connection and one transaction; the unit of work is internal. Both providers implement every supported application command and query; SQLite is not permitted to expose a feature-limited facade.

Author every read and write in LINQ/EF first. Only where EF genuinely cannot express or plan the operation — recursive hierarchy and prerequisite queries, PostgreSQL range/exclusion operations, deferred-constraint workflows, advisory locks, database-wide overlap discovery, and canonical cost-input queries — drop to hand-authored SQL, and on PostgreSQL encapsulate that SQL as a source-controlled stored function or procedure deployed by the migration scripts and invoked *through* EF: `modelBuilder.HasDbFunction(...)` for composable UDFs/table-valued functions so they participate in LINQ and plan review, `FromSql`/`SqlQuery`/`ExecuteSql` for non-composable logic. Inline raw-SQL string literals scattered through the persistence assembly are not the pattern; the SQL is a named database object with a stable logical identifier for error translation, and each function has direct integration tests. SQLite, lacking stored procedures, uses its enforcement triggers plus minimal parameterized statements. Do not force graph or interval algorithms through large tracked object graphs.

The EF model configuration shared by both providers (entity mappings, value converters, concurrency-token setup, `HasDbFunction` registrations) lives in one internal shared configuration component referenced by both persistence assemblies, so the two providers cannot drift independently of each other or of the reviewed SQL schema; only genuinely provider-divergent mapping (function bodies, temporal/decimal encoding, upsert/`RETURNING` differences) is specialised per provider.

Source-controlled versioned SQL deployment scripts remain authoritative. Do not use `EnsureCreated`, automatic startup upgrades, or generated EF migrations as the production schema mechanism. Add model-to-schema validation tests so EF mappings cannot silently drift from the reviewed SQL schema. The explicit schema deployment tool applies provider-specific forward-only scripts.

Account explicitly for SQLite provider limitations: no schemas or sequences, no database-generated concurrency token, limited native temporal/decimal ordering, and schema changes which require table rebuilds. Retain the canonical integer UTC instant encoding, fixed-precision money encoding, application-managed `bigint` version, provider-specific temporal/rate SQL, and explicit SQLite schema-version scripts. Never allow unsupported LINQ to fall back to client-side filtering of security-, integrity-, or cost-relevant data.

Pin EF Core and each provider to compatible major and patch versions. Provider upgrades require the complete database conformance suite, generated-SQL/query-plan review for critical queries, and schema-upgrade compatibility tests.

Cost queries capture `asOf` once and use a consistent snapshot. They discover overlapping sessions for relevant workers across the entire database, not only the requested subtree. Persistence materializes immutable cost inputs; the pure domain engine calculates results.

Translate constraint failures to stable public exceptions. A pre-check and a database race must produce the same public error category.

### 7.5 Library quality gates

- Public API approval tests and compatibility baselines pass.
- Architecture tests prohibit forbidden project references and public provider types.
- Domain unit/property tests and both provider conformance suites pass.
- Mutation tests cover critical interval, authorization, prerequisite, and costing rules.
- Consumption is tested from a separate sample application for each provider, even though the library is an internal project component rather than a supported external NuGet product.
- Cancellation, timeout, disposal, retry boundaries, and telemetry are integration-tested.
- NuGet package metadata, symbols, source link, deterministic output, and dependency vulnerability checks pass.
- No ASP.NET Core dependency exists in the reusable library layers.

The library gate must pass before any ASP.NET front-end feature implementation begins. The front end consumes the accepted library contract and may not compensate for an incomplete library by adding domain SQL, persistence access, authorization rules, or costing behaviour locally.

## 8. Phase 3 — modern ASP.NET Core application

### 8.1 Web architecture

Use server-rendered ASP.NET Core Razor Pages for the browser application. Organize pages by user workflow rather than database table, use partials and view components for repeated server-rendered elements, and keep page models thin by calling the JobTrack library. Use standards-based HTML with small JavaScript modules only for progressive enhancement such as confirmation dialogs, inline disclosure, and partial refreshes. Core workflows must not depend on a client-side application framework.

Use the stable Bootstrap release pinned at implementation time as the responsive and accessible presentation foundation. Define a small JobTrack design system through Bootstrap Sass variables and application components for typography, spacing, colour, focus, validation, tables, navigation, and status indicators; do not retain an unmodified default-Bootstrap appearance. Keep custom CSS layered and component-scoped, and include only required Bootstrap JavaScript components.

The visual direction is an operational work map, not a generic administration dashboard. Hierarchy lines communicate parentage; blocker markers and dependency relationships communicate readiness; time bands communicate schedules and concurrent work; and achievement uses redundant text, shape, and colour. Cost totals link to their provenance rather than appearing as unexplained metrics. Spend visual distinctiveness on hierarchy and temporal allocation while keeping forms and administration restrained.

Use a persistent job-context region on wider screens to keep the selected node, ancestry, children, prerequisites, active work, and status understandable together. On narrow screens, convert that context into ordered, collapsible sections without losing hierarchy or forcing a desktop canvas. Validate this signature interaction with users before propagating it across pages.

Design mobile-first. Pages shall reflow cleanly from narrow phone screens to desktop without horizontal page scrolling. Navigation collapses accessibly; forms use single-column layouts on narrow screens; actions remain reachable without hover; touch targets are appropriately sized; and dense hierarchy, audit, and cost tables switch to deliberate small-screen representations such as summaries, cards, disclosure rows, or bounded internal scrolling. Do not merely shrink desktop tables.

Set and test explicit representative viewport classes for small phones, larger phones, tablets, and desktops. Browser tests cover resize/reflow, keyboard operation, visible focus, zoom, orientation changes, long content, validation messages, and the absence of clipped controls or unintended horizontal overflow. Automated accessibility checks supplement, but do not replace, manual keyboard and screen-reader review of critical workflows.

Meet WCAG 2.2 AA as the minimum accessibility target. Prefer semantic HTML before ARIA, preserve logical reading and focus order, never rely on colour alone, respect reduced-motion and forced-colour preferences, and test text resize and 400% browser zoom. Announce progressively enhanced validation and status changes only where asynchronous behaviour requires it.

Do not build a SPA or introduce Blazor for the initial release. The site's modest interactivity does not justify a second client-side application model, duplicated transport state, or a token-based browser authentication flow. Reconsider this only if measured requirements later demand sustained real-time interaction or substantial offline/client-side behaviour.

Use same-origin Identity cookie authentication for browser pages and the initial HTTP API. This is adequate for the single-server, employee-only site when combined with secure cookie attributes, antiforgery protection, security-stamp validation, lockout, rate limiting, and HTTPS. Do not add bearer-token issuance until a concrete non-browser remote API consumer requires it. Internal CLI and worker processes consume the library directly rather than authenticating through HTTP.

The initial content model is plain text. Attachments, file uploads, and user-authored HTML are explicitly out of scope.

Before multiplying page templates, design one representative job-detail workflow to production quality. Define and review its loading, empty, validation, authorization-denied, missing, optimistic-conflict, failure, and success states. Establish design tokens and reusable Razor partials/view components only from demonstrated repetition, then apply them to subsequent workflows.

Interface copy uses domain language rather than schema names, active and specific labels such as “Start work” and “Add prerequisite,” and the same verb through action, confirmation, and audit display. Empty states identify a valid next action. Errors state the failed rule and the available correction without exposing internal details.

### 8.2 Security architecture before UI work

Produce a threat model and abuse-case test plan covering credential stuffing, account enumeration, session theft, CSRF, XSS, authorization bypass, insecure direct object references, subtree-scope confusion, mass assignment, sensitive logging, database credential compromise, and emergency reset abuse.

Use ASP.NET Core Identity for locally managed employee accounts, with the credential tables separated from `app_user`. There is no public registration or automated end-user recovery. Implement Identity through `JobTrack.Identity`, composed by the web and administrative CLI hosts. This adapter owns the production Identity `DbContext`, stores, password hasher integration, security-stamp operations, and provider registration. It may access only credential/account tables and the narrow employee-account link; it does not gain access to domain tables or become an alternative domain persistence path. Implement:

- supported Identity password hashing only;
- secure, `HttpOnly` authentication cookies with an explicit `SameSite` policy, bounded lifetime, renewal, and security-stamp validation;
- login rate limiting and bounded lockout with generic failures;
- antiforgery protection on state-changing browser requests;
- strict transport security, HTTPS redirection, forwarded-header trust boundaries, and secure proxy configuration;
- a restrictive Content Security Policy, frame restrictions, MIME sniffing protection, referrer policy, and carefully scoped cross-origin policy;
- output encoding and sanitized rendering for user-authored rich content, or plain text until a sanitizer is justified;
- request/body limits, endpoint timeouts, cancellation propagation, and bounded pagination/report ranges;
- data-protection keys persisted in protected durable host storage;
- security-stamp revocation after disablement, reset, password change, and security-sensitive role changes; and
- secret-free authentication and administration audit events.

Reserve an extension seam for passkeys but do not expose a non-functional enrolment flow in the first release.

### 8.3 Authorization model

Define named, default-deny policies for Administrator, Job manager, Worker, Rate manager, Cost viewer, and Auditor/read-only capabilities. Roles provide coarse admission only. The library reloads authoritative account state, assignments, ownership, subtree scope, target user, and data sensitivity inside each operation.

Prevent sensitive data from being loaded or serialized when the caller lacks cost/rate permission. Test authorization using direct crafted HTTP requests, not only hidden UI controls. Use opaque route binding to strongly typed identifiers and allow-list model binding into command-specific request models to prevent mass assignment.

### 8.4 HTTP API

Design resource-oriented endpoints around user intent rather than table CRUD. Publish an OpenAPI contract with:

- descriptive status values and ISO 8601 timestamps with offsets;
- optimistic-concurrency tokens and explicit conflict behaviour;
- RFC 7807 problem details mapped from stable library failures;
- pagination, filtering, range limits, and cancellation;
- idempotency strategy for retry-prone commands where duplicate execution is harmful; and
- no database constraint names, provider details, secrets, or legacy status codes.

Keep transport DTOs separate from public library contracts so HTTP compatibility can evolve deliberately. Version the API only when a real compatibility requirement exists.

### 8.5 Browser experience

Deliver accessible, progressively enhanced workflows in vertical slices:

1. sign-in, forced password change, logout, and access-denied handling;
2. job tree browsing, search, ownership, archive filters, and readiness explanations;
3. create/edit/move/decompose workflows with concurrency-conflict recovery, each showing an explicit impact/destination preview before commit (what re-parents, what the new children are, what history is preserved) for these highest-risk structural operations;
4. leaf work, session start/pause/resume/finish, and audited correction — concurrent sessions for the same user are valid, so the UI warns about overlap without prohibiting it;
5. prerequisite editing and achievement updates;
6. personal schedule and exception management;
7. authorized employee, schedule, and rate administration;
8. cost reports with rate provenance and current prerequisite diagnostics;
9. audit browsing with permission-sensitive detail; and
10. administrator account provisioning, disablement, reset, and revocation.

Each slice starts with an HTTP integration or browser test, then endpoint/policy work, then the UI. The UI never receives credentials, hashes, security stamps, unrestricted rate data, or database objects.

### 8.6 Administrative CLI

Implement a narrowly scoped CLI for:

- explicit schema deployment/validation;
- one-time atomic bootstrap of the first administrator, permanent root posted and owned by that administrator, and initialised marker from protected interactive input — the CLI collects the secret and *invokes the library bootstrap command* (§7.3 step 1); it does not reimplement the transaction, and supplies the concrete `IPasswordHasher<T>` at composition; and
- emergency password reset using a separate least-privileged database role.

The reset command uses `JobTrack.Identity` and the configured Identity password hasher, creates a single-use short-lived reset path or temporary credential, forces password change, revokes sessions and prior grants, and emits an audit event without printing or storing reusable secrets beyond the controlled handoff.

### 8.7 Web gate

- Authentication, revocation, lockout, antiforgery, security headers, and enumeration tests pass.
- Every endpoint has an explicit authorization policy and direct-request negative tests.
- Role combinations, ownership boundaries, own-session/schedule rules, and independent cost visibility pass.
- Problem details disclose no internal or sensitive information.
- End-to-end tests cover the complete operational scenarios in both PostgreSQL and SQLite configurations.
- Responsive browser tests pass at the agreed phone, tablet, and desktop viewports without unintended page overflow, clipped actions, or desktop-only workflows.
- Accessibility, keyboard navigation, browser compatibility, performance, and dependency/security scans meet agreed budgets.
- The web project has no direct reference to provider implementation APIs beyond composition registration and no SQL.

### Phase 1–3 gate status

Formal, source-controlled acceptance records exist for all three gates above, in the order they
were passed:

1. **M3 (database, §6.7)** — `docs/decisions/0025-m3-database-gate-acceptance.md`, Accepted.
2. **M6 (reusable library, §7.5)** — `docs/decisions/0026-m6-library-gate-acceptance.md`, Accepted.
   Depends on M3.
3. **M8 (web, §8.7)** — `docs/decisions/0027-m8-web-gate-acceptance.md`, Accepted. Depends on M6 and
   on every previously `pending`/`partial` row in `docs/traceability/test-catalogue.md` being closed.

Each ADR records evidence per exit-criterion bullet rather than restating this plan; consult the ADR
for the concrete test classes and documents backing a given bullet, not this section.

## 9. Phase 4 — production hardening and release

### 9.1 Operational readiness

Deploy directly to one modest server without containers. Run the ASP.NET Core application as a dedicated unprivileged operating-system service behind a locally managed reverse proxy, with PostgreSQL on the same server or a directly managed database host and SQLite as a mutually exclusive full-backend deployment choice. Bind Kestrel to a private loopback endpoint or local socket, terminate HTTPS at the reverse proxy, restrict filesystem and database permissions, and persist data-protection keys in a protected host directory. Do not introduce distributed caches, orchestration, or multi-node coordination until measured capacity or availability requirements justify them.

Create and rehearse runbooks for:

- PostgreSQL provisioning, schema deployment and upgrade, backup, restore, point-in-time recovery, failover, vacuum/statistics, connection saturation, and index maintenance;
- SQLite safe file placement, permissions, backup API usage, restore, corruption checks, and single-writer limitations;
- key rotation, data-protection key recovery, application/database credential rotation, and emergency reset;
- audit export/retention and privileged-access review;
- rollback by application deployment while retaining forward-only database compatibility; and
- incident response for suspected account or database compromise.

Use least-privilege runtime identities, an external secret store, encrypted transport, encrypted backups, dependency provenance, signed release artefacts where supported, and environment-specific security configuration. Do not put production secrets or default credentials in deployment scripts, configuration files, or logs.

### 9.2 Observability

Add OpenTelemetry traces, metrics, and structured logs for operation name, actor identifier, correlation identifier, affected identifiers, duration, outcome, retry/contention, and database timing. Redact rates, cost details, reset material, credentials, cookies, tokens, and unrestricted personal data.

Define alerts and service objectives for authentication failures, lockouts, authorization denials, schema-version mismatch, database pool pressure, lock contention, slow cost reports, error rates, audit-write failures, and backup/restore freshness.

Audit writes are part of the business transaction for sensitive changes; a failed required audit write fails the command.

### 9.3 Performance and resilience

Establish measurable budgets before tuning. Exercise realistic scale for tree reads, overlap discovery, cost calculation, concurrent writes, login bursts, and audit queries. Test cancellation and bounded resource use for deliberately expensive ranges. Do not introduce a PostgreSQL-only cost algorithm unless differential tests prove it equivalent to the pure engine and SQLite remains conformant.

Retry only transient failures and only at a boundary where replay is safe. Do not retry invariant, authorization, concurrency, or prerequisite failures. Make shutdown drain in-flight requests and database work within a bounded period.

### 9.4 Release gate

The initial release targets a single modest server for an employee-only internal system. A baseline production-scale performance run and successful backup/restore smoke rehearsal are release blockers. External penetration testing, mutation-coverage thresholds, sustained load rehearsals, failover exercises, and full disaster-recovery drills may be deferred behind a documented, signed-off risk acceptance, provided the security baseline of §8.2–§8.3 and the mandatory release criteria below are met.

Release only when:

- every authoritative acceptance criterion has linked passing evidence;
- both provider conformance suites and PostgreSQL production-scale tests pass;
- threat-model mitigations are verified, and any available penetration-test findings are resolved or explicitly risk-accepted;
- dependency, secret, static-analysis, and dynamic security scans pass;
- a production-like backup has passed a restore smoke rehearsal and integrity verification;
- upgrade from every supported schema version has been rehearsed on production-like copies;
- public API and HTTP compatibility reports have been reviewed;
- operational dashboards, alerts, runbooks, and on-call ownership exist; and
- no known high-severity security or data-integrity defect remains.

## 10. CI pipeline

Use separate jobs so fast feedback does not wait for full-system tests:

1. restore verification, formatting, analyzers, architecture tests, and public API diff;
2. domain and application unit/property tests;
3. PostgreSQL schema-deployment, integrity, race, query-plan, and conformance tests;
4. SQLite schema-deployment, integrity, transaction, and conformance tests;
5. web integration, authorization, and antiforgery tests;
6. browser end-to-end tests;
7. package-consumer and deployment smoke tests;
8. dependency, secret, source, host-deployment, and dynamic security scans; and
9. scheduled generated-scale, mutation, load, restore, and long-running resilience tests.

Every test invocation uses `gtimeout` with a budget appropriate to its category. CI preserves logs, test results, query plans, failing generated seeds, schema checksums, coverage, security reports, and release provenance without preserving secrets.

## 11. Initial milestone sequence

| Milestone | Outcome | Gate evidence |
|---|---|---|
| M0 Foundation | Pinned toolchain, decisions, test catalogue, traceability | Clean build and accepted ADRs |
| M1 Database core | Users, hierarchy, leaf work, sessions on both engines | Integrity and race suites |
| M2 Database temporal | Prerequisites, schedules, exceptions, rates, audit, canonical queries | Provider equivalence and plans |
| M3 Database gate | Deployable schemas and operations | Full database gate review |
| M4 Domain library | Pure time, achievement, rates, and costing | Unit/property tests |
| M5 Application library | Commands, authorization, auditing, persistence providers | Provider conformance suites |
| M6 Library gate | Stable FDG-reviewed package | API, architecture, package tests |
| M7 Secure web foundation | Identity, policies, revocation, HTTP error model | Security integration tests |
| M8 Operational web | Job/work/schedule/rate/cost/audit vertical slices | Direct HTTP and browser tests |
| M9 Release candidate | Hardened deployment and administration | Security, scale, restore evidence |
| M10 Production release | Accepted system and runbooks | Full acceptance traceability |

Milestones are dependency-ordered, not calendar estimates. Estimate them only after M0 decomposes each testable slice and the team has measured delivery throughput.

## 12. Risks and controls

| Risk | Control |
|---|---|
| Cross-table invariants differ between providers | Shared contract tests from phase 1; SQLite immediate transactions; stable error categories |
| Deferred PostgreSQL constraints hide race or lock problems | Explicit concurrent tests, deterministic lock ordering, measured advisory locks |
| Cost allocation becomes inconsistent or too slow | Pure canonical engine, property tests, database-wide discovery tests, scale fixtures, differential optimization tests |
| DST behaviour changes historical calculations unexpectedly | Frozen resolution policy, IANA zone snapshot/version awareness, transition-boundary golden tests |
| Public library leaks persistence or becomes difficult to evolve | Consumer-first API specs, FDG review, architecture tests, compatibility baselines |
| HTTP policy is mistaken for complete authorization | Coarse web policy plus authoritative library authorization inside transactions |
| Sensitive rates or credentials leak through projections or telemetry | Permission-specific queries/DTOs, log redaction tests, secret-free audit schemas |
| SQLite is mistaken for a reduced or high-concurrency backend | Full contract conformance, documented single-writer envelope, and distinct operational/load acceptance targets |
| Automatic deployment damages production schema | Separate schema-deployer identity, checksums, compatibility check only at app startup |
| Dynamic historical costing is mistaken for accounting finality | Explicit UI/API language and no stored “final actual cost” representation |
| Decimal rounding of per-session shares breaks time-conservation | Carry allocated time as exact rational `(ticks, N)`; single rounded money division `rate × ticks ÷ (N × ticksPerHour)`; conservation property test asserts exact equality (§5.1 item 6) |
| Database-wide `N` discovery leaks out-of-scope work | Internal elevated read scope for `N` only; expose requested-job aggregates, never foreign sessions/nodes/rates; explicit negative test (§5.1 item 16) |
| Atomic bootstrap blocked by front-end-phase Identity | Bootstrap is a library command using `Microsoft.Extensions.Identity.Core` `IPasswordHasher<T>` + a credential-write port, testable in M5 (§5.1 item 2) |
| Irreducible SQL sprawls as inline strings | EF-first; irreducible PostgreSQL logic encapsulated as versioned stored functions/procedures invoked through EF with stable logical identifiers (§5.1 item 7, §7.4) |

## 13. First implementation increment

After approval of this plan, execute M0 first. Do not begin or freeze the first M1 schema slice until the M0 gate in §5.5 has been accepted.

1. create the solution, shared build configuration, and test projects;
2. complete and accept every ADR and product decision in §5.1;
3. establish traceability, test timeout budgets, performance datasets, and measurable scale budgets;
4. complete the risk spikes in §5.3, including concurrent independent-connection proofs;
5. record formal M0 acceptance;
6. create failing schema-deployment tests for empty PostgreSQL and SQLite databases;
7. implement schema-version metadata, reference-data deployment, and Identity storage on both providers;
8. prove account creation and revocation through a throwaway Identity compatibility spike which is not referenced by the reusable library;
9. create failing tests for the atomic administrator/root/initialised bootstrap, permanent-root guards, and optimistic versioning;
10. implement those constraints for PostgreSQL and SQLite; and
11. add concurrent and raw-bypass tests before proceeding to general hierarchy operations.

This increment establishes the working TDD and provider-conformance pattern before the schema grows.

## 14. Review prompts

Use these prompts during fresh-eyes gate reviews.

### 14.1 Database and domain

- Which authoritative invariant or query contract does this change implement?
- Does enforcement remain correct under concurrent independent connections?
- Can a transaction expose or commit an invalid intermediate state?
- Are interval boundaries half-open, timezone-safe, and tested at exact equality?
- Are derived values reproducible and disposable rather than authoritative caches?
- Is each index justified by a measured query plan?
- Do PostgreSQL and SQLite produce the same public effect or stable failure category?

### 14.2 Library API

- Does representative consumer code read naturally without persistence knowledge?
- Is the common secure scenario the simplest call path?
- Can any public type, member, overload, Boolean, or extension point be removed?
- Are naming, nullability, cancellation, collections, exceptions, and compatibility consequences explicit?
- Do provider types or mutable persistence models leak across the boundary?
- Is the code immutable-first and functional in the domain core, with exhaustive matching over closed enums and no silent `default` fallthrough?
- Does every failure throw a .NET exception (framework type or the `JobTrackException` hierarchy), with no error-code returns and no `Result`/`Either` error channel anywhere? Where a `Try*` member exists, is it justified by FDG's performance rationale (a measured hot path or common-failure case, à la `TryParse`) complementing a throwing member — not a smuggled failure-category channel or a reflex for every query?

### 14.3 Interface

- What is the page's single primary job?
- Are hierarchy, blockers, active work, and cost provenance explained rather than merely marked?
- Are loading, empty, denied, missing, validation, conflict, failure, and success states useful?
- Does the workflow remain complete by keyboard and at narrow phone width?
- Is status understandable without colour, and does copy use stable user-facing vocabulary?
- Is every decorative or animated element encoding information or improving comprehension?
