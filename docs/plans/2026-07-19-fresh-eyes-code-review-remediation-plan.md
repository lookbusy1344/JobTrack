# Fresh-Eyes Code Review Remediation Plan

**Date:** 2026-07-19
**Status:** All findings implemented — §2.1 through §2.8.
**Scope:** A hostile full-stack review of the current tree after requester intake, temporal
hardening, ownership, and multi-level Browse work. This plan contains only findings independently
confirmed against the implementation and current normative plans/ADRs; it does not reopen items
already closed by earlier remediation plans.

## 1. Review baseline

The working tree was clean at review start. Review covered the authoritative specification and
accepted ADRs, Application/public contracts, both persistence providers, schema scripts, Identity,
Razor Pages, the external HTTP API, tests, and operational scripts. The following checks passed on
2026-07-19:

- `gtimeout 300 dotnet build JobTrack.slnx -warnaserror` — 0 warnings, 0 errors.
- `gtimeout 300 ./scripts/fast-test.sh --build` — 1,078 tests passed in 19 seconds.

Those gates establish a sound baseline, but they do not exercise malformed/inferred civil-time
binding, unknown-login audit-principal races, unbounded audit history, PAT refresh replay, or
row-proportional cost-query fan-out.

Three findings from the first review pass have already landed:

- §2.1: commit `d96f19c0` (`fix(web): reject malformed civil-time input`).
- §2.2: commit `ebdc2e35` (`fix(web): reject unknown viewer time zones`).
- §2.4: civil-time boundary completed across Jobs Create/Edit/Decompose, Admin Rates/
  CorrectUserCostRate/CorrectNodeRateOverride, and the cost report's `AsOf` bound.

They remain in this plan as completed review evidence. Do not implement them again.

## 2. Findings

### 2.1 Malformed optional `datetime-local` input is silently reinterpreted — implemented

**Severity:** High
**Category:** Interface / temporal correctness
**Status:** Implemented by `d96f19c0`

`BackdateInstant.TryParse` originally returned `false` for both an omitted value and a malformed
value. State-changing session handlers collapsed those different cases, so a malformed non-empty
value could start or finish work at “now”, or reopen a corrected session. Audit filters similarly
dropped malformed bounds and broadened the query.

The implementation now uses `BackdateInstant.TryParseOptional`, rejects malformed non-empty input,
and has direct-HTTP integration coverage across Audit, Browse, Awaiting Progress, Work, and session
correction. Blank optional values retain their documented meaning.

### 2.2 Viewer time-zone resolution hides persisted-zone rot — implemented

**Severity:** High
**Category:** Interface / temporal correctness
**Status:** Implemented by `ebdc2e35`

`ViewerTimeZoneResolver.ResolveAsync` originally substituted `Etc/UTC` when an employee's persisted
IANA zone was absent from the bundled TZDB. The web host could therefore render and parse in the
wrong zone instead of surfacing persisted-data rot.

The resolver now throws `UnknownStoredTimeZoneException`, matching the reusable-library policy, and
integration coverage proves the page does not continue using UTC.

### 2.3 Audit search is unbounded before projection, paging, or rendering — implemented

**Severity:** High
**Category:** Library / persistence / operational resilience
**Status:** Implemented
**Files:** `AuditEventSearchFilter.cs`, `IAuditQueries.cs`, `IAuditQueryPort.cs`, both
`*AuditQueryPort.cs` files, `Pages/Audit/Index.*`

Both providers execute `ToListAsync` over every matching `audit_event`; `AuditQueries` then projects
the entire set, and the Razor page renders it in one response. The filter has no cursor, offset,
limit, or mandatory time window. An empty authorized search therefore reads, allocates, parses JSON,
redacts, and renders the complete append-only audit history. This is an authenticated availability
failure and conflicts with implementation-plan §9's bounded-resource requirement.

**Remediation:**

1. Add failing Application tests for a bounded page contract before changing the public API.
   Prefer an opaque keyset cursor ordered by `(OccurredAt DESC, Id DESC)`; define named default and
   maximum page sizes and return a continuation cursor only when `Take(max + 1)` finds another row.
2. Change the port contract and both EF assemblies so filters, cursor predicates, ordering, and the
   limit execute in SQL before entities or JSON payloads are materialized.
3. Retain authorization before the heavy query. Remove the now-redundant second actor-role load and
   `ActorRoles` member from `AuditSearchQueryResult` unless a documented snapshot requirement
   justifies it.
4. Update the Razor page to preserve filters across continuation links. The external API currently
   excludes audit search; keep that exclusion explicit rather than expanding API scope incidentally.
5. Add shared provider-contract tests for adjacent pages, equal-timestamp ID tie-breaking, filter
   application before limiting, cancellation, and a large fixture that proves bounded
   materialization.

**Acceptance:** No audit request can materialize more than `MaxPageSize + 1` persistence rows; pages
are stable and non-overlapping while newer events are appended.

`AuditEventSearchRequest` now carries an optional opaque `Cursor`/`PageSize`; `AuditQueries` resolves
the effective page size against the new `AuditSearchPaging.DefaultPageSize`/`MaxPageSize` constants,
asks `IAuditQueryPort` for `pageSize + 1` rows past the decoded `AuditEventSearchCursor` boundary, and
returns `AuditEventSearchResult` (events plus an opaque `ContinuationCursor`, `null` once the probe row
is absent). Both providers' `AuditQueryAssembly.SearchAsync` push the `(OccurredAt, Id)` keyset bound
and `Take(limit)` into the EF query before materialization; `AuditEventId` gained `IComparable`/
relational operators so `e.Id < before.Id` translates to SQL (EF Core does not translate `.Value`
member access through a `ValueConverter`, which the failing-test-first slice surfaced). The redundant
second actor-role load is gone: `IAuditQueryPort.SearchAuditEventsAsync` no longer takes `actorId`, and
`AuditSearchQueryResult` no longer carries `ActorRoles`. `Pages/Audit/Index` renders a "Next page" link
that round-trips every filter plus the opaque cursor and rejects a malformed cursor with a page-level
error instead of throwing. Coverage: `AuditQueriesTests`/`FakeAuditQueryPort` (paging, clamping, tie-
breaking, malformed-cursor rejection), `AuditQueryPortContractTestsBase` against both providers (non-
overlapping pages, equal-timestamp tie-breaks, filter-before-limit, a 250-row bounded-materialization
fixture), and a direct-HTTP web test proving the continuation link works and stops once exhausted.

### 2.4 Several Razor forms still bind instants through the server's local time zone — implemented

**Severity:** High
**Category:** Interface / temporal correctness
**Status:** Implemented
**Files:** `Pages/Jobs/{Create,Edit,Decompose,CostReport}.*`,
`Pages/Admin/{Rates,CorrectUserCostRate,CorrectNodeRateOverride}.*`

The completed §2.1 fix covered fields explicitly written as `type="datetime-local"`, but 15
`DateTimeOffset`-typed Razor fields remain. The tag helper infers local date/time inputs for the
relevant fields and model binding resolves offset-less values using the server process's zone.
Confirmed affected instants include:

- job `NeededStart` / `NeededFinish` on create, edit, and decompose;
- rate and node-override effective start/end on create and correction; and
- the cost report's optional `AsOf` bound.

This directly violates the repository rule that a Razor Page field representing an instant is a
`string` parsed through `BackdateInstant` in the viewing employee's zone. A deployment whose server
zone differs from the employee's zone persists or queries the wrong instant. The same pages also
render some instants directly: rate effective periods and cost trace segment boundaries bypass
`InstantDisplay` and show raw Noda Time values instead of the viewing employee's zone.

**Remediation:**

1. Add direct-HTTP integration tests first, with the server process and viewing employee in
   deliberately different zones. Prove the resulting `Instant` follows the employee's zone,
   including a DST fold/gap case through `CivilTimeResolver`.
2. Replace every instant-bound `DateTimeOffset` Razor property with a string input. Resolve
   `ViewerZone` and use `BackdateInstant`/`TryParseOptional`; reject malformed non-empty values
   without invoking a command or broadening a report.
3. On GET/edit flows, format stored instants back to `datetime-local` text in `ViewerZone`; do not
   round-trip through the server zone. Keep actual date-only schedule fields as `LocalDate` — they
   are not instants and are outside this change.
4. Resolve the viewer zone on Rates and Cost Report, then render every `Instant` with
   `InstantDisplay`. Add a small architecture/static test that rejects `DateTimeOffset` properties
   in Razor Page input models and direct raw-`Instant` rendering in `.cshtml`, with an explicit
   allowlist only for true offset-bearing HTTP boundary DTOs outside Razor Pages.
5. Run the end-to-end axe suite after the form changes.

**Acceptance:** Changing the host OS time zone cannot change any stored/query instant for identical
employee input, and no Razor page displays an `Instant` without the viewer's explicit zone.

All seven listed `DateTimeOffset` Razor properties (job `NeededStart`/`NeededFinish` on Create/Edit/
Decompose, rate/override `EffectiveStart`/`EffectiveEnd` on Rates and both Correct pages, and Cost
Report's `AsOf`) are now `string` fields parsed via `BackdateInstant.TryParse`/`TryParseOptional` in
`ViewerZone`, rendered as `type="datetime-local"`, and pre-filled on GET via
`BackdateInstant.ToDateTimeLocalValue`. Rates and Cost Report resolve `ViewerZone` and render rate
periods and cost-trace segment boundaries through `InstantDisplay`. `WebHostCivilTimeArchitectureTests`
guards both rules going forward. Direct-HTTP integration tests cover malformed-input rejection and a
server/viewer zone mismatch (including a DST spring-forward gap) for Create, Edit, Decompose, Cost
Report, and Rate administration; the end-to-end axe/keyboard suite passes unchanged (one keyboard test
updated to fill the now-genuinely-blank `datetime-local` field instead of relying on the old
`DateTimeOffset` default's incidental `0001-01-01T00:00` pre-fill).

### 2.5 Accepted `IClock` policy is absent from the runtime composition — implemented

**Severity:** High
**Category:** Cross-cutting correctness / testability / ADR compliance
**Status:** Implemented (persistence/Application layer: commit `56c7b889`; Web/AdminCli/Identity/
Database: commit `bc945931`)
**Files:** provider factories, Application query/command implementations, both persistence
providers, Identity, Web, AdminCli, Database deployer

ADR 0016 requires DI-registered Noda Time `IClock` as the sole source of “now”, captured once at the
start of each operation. The current runtime has zero `IClock` references, 122 direct
`SystemClock.Instance.GetCurrentInstant()` reads, and four `DateTimeOffset.UtcNow` reads. Public
provider factories do not accept a clock, so provider contract tests must use real time and
time-sensitive paths cannot be deterministic.

Some operations also re-read the clock inside helpers or after database work rather than sharing
one captured value. Examples include account-state authorization plus mutation timestamps,
job-node commands that stamp entities and audit rows independently, and Identity's 2FA timestamp.
This permits internally inconsistent timestamps at a boundary and makes expiry/lockout/future-time
tests race wall-clock time.

**Remediation:**

1. Start with architecture tests that forbid direct runtime `SystemClock`/`UtcNow` use outside the
   approved composition roots that register/pass `SystemClock.Instance`. Add deterministic tests with
   `FakeClock` before changing implementation.
2. Add an `IClock` dependency to Application services and persistence ports. Extend
   `JobTrackPostgreSql.Create` and `JobTrackSqlite.Create` with an optional `IClock` parameter that
   defaults to `SystemClock.Instance`, preserving source compatibility while creating a real test
   seam. Review the additive public API against the FDG/public-API gate.
3. Capture `now` once per public operation, after entering its transaction where applicable, and
   thread that value through account-state checks, entity timestamps, audit rows, expiry decisions,
   and helper methods. Do not let nested services independently recapture it.
4. Inject the same clock into Web, AdminCli, schema deployment, authentication auditing, PAT
   issuance, and Identity time-sensitive adapters. Convert to `DateTimeOffset` only at the Identity
   boundary.
5. Add provider-conformance tests with a fixed clock proving entity and audit timestamps are exactly
   equal and lockout/PAT/session boundary decisions are stable. Advance the fake clock explicitly
   for expiry tests rather than sleeping or comparing “before/after” real times.

**Acceptance:** Runtime source contains no direct wall-clock reads outside approved composition;
every time-dependent operation has one observable captured instant and deterministic provider tests.

**Implementation notes:** `ClockCompositionArchitectureTests` (allowlist-based, greps `src/` for
`SystemClock.Instance`/`DateTimeOffset.UtcNow`) landed first and stayed red until every runtime
call site took `IClock` as an explicit dependency; both provider factories default an optional
`IClock? clock = null` to `SystemClock.Instance` and thread it through every command/query port,
capturing `now` once per public operation and passing it into the account-state authorization check
and every entity/audit-row stamp in that operation. `SchemaDeployer` is treated as its own
default-binding composition root (like the two provider factories) rather than threading a clock
through its ~100 call sites, since its one read is deployment-tooling bookkeeping
(`AppliedSchemaVersion.AppliedAtUtc`), not a business-domain instant. Follow-up review added shared
PostgreSQL/SQLite provider contracts with an adjustable counting clock: session start proves one
clock read supplies both the entity and audit timestamp, PAT authentication proves deterministic
behaviour immediately before and exactly at expiry, and issuance proves the exact lockout-end
boundary is admitted without wall-clock comparisons.

### 2.6 Unknown-login audit events use a collidable, racy pseudo-principal — implemented

**Severity:** High
**Category:** Database / security / audit integrity
**Status:** Implemented
**Files:** both `*AuthenticationAuditPort.cs`, schema version 0012, authentication audit tests

An unknown-username failure needs a non-null `audit_event.actor_user_id`. Both providers satisfy
that by querying `app_user.DisplayName == "JobTrack authentication audit"` and creating a row when
none exists. `display_name` is intentionally not unique and is ordinary user data.

Consequences:

- an administrator can create a real employee with that display name first, causing anonymous
  failures to be attributed to that employee's `app_user` id;
- a duplicate display name makes `FirstOrDefaultAsync` choose an arbitrary matching row; and
- concurrent first unknown-login failures can both observe absence and race to create duplicate
  pseudo-principals (or fail the audit write), because no database key or bootstrap invariant makes
  the get-or-create operation unique.

The append-only event is then permanently attached to the wrong or non-canonical actor. This is an
audit-integrity defect, not merely a naming issue.

**Remediation:**

1. Decide the actor model before code. Prefer making `audit_event.actor_user_id` nullable for events
   where no authenticated actor exists, while retaining a non-blank bounded subject marker in the
   already-redacted payload. If the product requires a system principal instead, add an explicit
   schema-owned identity/key and uniqueness invariant; never discover it by display text.
2. Follow database-first TDD: shared schema contract, PostgreSQL enforcement, SQLite enforcement,
   then a provider-specific concurrent first-write test. Because the project is pre-release, edit
   schema versions 0002/0012 in place as appropriate.
3. Update `AuditEventRecord`/public projection for an optional actor if that decision is selected;
   render “system/unknown” without fabricating an employee identity. Review this compatibility
   change against the public API gate.
4. Add web integration tests for a colliding employee display name and simultaneous unknown-login
   failures. Prove all attempts are recorded once, no employee is blamed, and no request fails due
   to audit-principal creation.
5. Amend the authentication threat model and traceability catalogue with the chosen actor
   semantics.

`audit_event.actor_user_id` is now nullable (schema version 0012, edited in place pre-release, both
providers), with the FK/index otherwise unchanged. Both `*AuthenticationAuditPort.cs` no longer
look up or create a "system actor" `app_user` row — an unknown-subject failure simply writes
`actor_user_id = NULL` with `entity_type = "authentication_attempt"`, a sentinel `entity_id`, and the
existing redacted `after_data` subject marker. `AuditEventRecord`/`AuditEventEntity`/`AuditEventResult`
carry an optional actor end to end (public API list updated); the Audit page renders a null actor as
"system" instead of dereferencing it. New coverage: a shared `AuthenticationAuditPortContractTestsBase`
(known-actor, null-actor, colliding-display-name, and concurrent-unknown-failure cases) asserted
against both providers, plus web-level tests for the display-name collision and simultaneous unknown
logins. No existing threat-model/traceability doc described the old system-actor behavior, so none
needed amending.

**Acceptance:** Unknown-subject authentication events cannot be attributed to a real employee,
cannot create duplicate pseudo-users, and remain reliable under concurrent failure traffic.

### 2.7 PAT issuance violates mandatory Post/Redirect/Get and can mint on refresh — implemented

**Severity:** Medium
**Category:** Interface / credential lifecycle
**Files:** `Pages/Account/PersonalAccessTokens.*`, `PersonalAccessTokenManagementTests.cs`

`OnPostIssueAsync` deliberately returns `Page()` after successfully persisting a PAT so it can show
the plaintext in the POST response. Refreshing that response can resubmit the form and mint another
live credential. This directly contradicts the repository's mandatory PRG rule for every successful
state-changing Razor handler. The current test cements the exception by expecting HTTP 200.

The plaintext must still be shown only once and must not be placed in cookie-backed `TempData`, a
URL, a log, or an unbounded process cache.

**Remediation:**

1. Add the failing integration/browser test first: successful issue returns a redirect; following
   it shows one plaintext token; refresh shows no plaintext and creates no additional token.
2. Introduce a bounded, short-lived, one-use server-side delivery store keyed by a cryptographically
   random handle and scoped to the actor. For the accepted single-server deployment an in-memory
   implementation is sufficient if it has a strict capacity, per-entry expiry, consume-on-read,
   and no logging. Never store the plaintext in cookie TempData.
3. Reserve bounded delivery capacity before calling `IssueAsync`; release the reservation if the
   command fails. Publish the returned plaintext into that already-owned slot and redirect to a GET
   carrying only the opaque handle. This avoids a second compensating `IJobTrackClient` call and the
   forbidden split compound-write shape. The GET atomically consumes the slot after verifying the
   signed-in actor. A missing/expired slot shows the token summary plus a clear “secret no longer
   available; revoke it if it was not copied” message. Document the unavoidable process-crash
   window between database commit and in-memory publication.
4. Replace the existing HTTP-200 expectation; this is a knowingly incorrect test relative to the
   now-explicit repository directive, so change it only as part of this approved remediation slice,
   never merely to make a failure pass.

**Acceptance:** Every successful PAT issuance follows PRG, refresh cannot issue or redisplay a
credential, plaintext exists server-side only for one bounded delivery window, and exhausted
delivery capacity prevents issuance before mutation.

### 2.8 Row cost enrichment fans out into one full calculation per result — implemented

**Severity:** High
**Category:** Application / persistence / operational resilience
**Status:** Implemented
**Files:** `JobQueries.cs`, cost-query contracts/providers, Awaiting Progress query/page tests

`EnrichSummariesWithCostAsync` and `EnrichAwaitingProgressWithCostAsync` call
`GetHierarchyTotalsAsync` once per visible row inside unbounded `Task.WhenAll`. Each call opens its
own provider context and cost snapshot and can perform the full overlap/rate/schedule materialization.

For HTTP children/search, the API asks the library for `pageSize + 1`, so a maximum page can launch
201 concurrent cost calculations before the continuation probe row is discarded. Awaiting Progress
is not paginated: it loads the full node/prerequisite graph, derives every matching leaf, and can
launch one calculation per leaf. The behavior scales database work and connection demand with row
count, can exhaust the pool, and defeats the project's set-based/no-N+1 performance policy.

**Remediation:**

1. Add Application tests with counting fakes proving cost enrichment performs a fixed number of
   calls as result width grows. Add a provider command-count/connection-concurrency test before
   changing the query shape.
2. Define a bounded bulk-cost contract that accepts the authorized candidate node IDs and one
   captured `asOf`, materializes the union of required inputs in one consistent provider snapshot,
   runs the pure engine once per necessary connected scope in memory, and returns a node-to-cost
   map. Preserve ADR 0042's per-row redaction; batching must not broaden cost visibility.
3. Apply pagination/caps before enrichment. Do not calculate cost for the `pageSize + 1`
   continuation probe. Add a bounded page contract to Awaiting Progress, preserving its current
   deterministic ordering and filters.
4. Remove `Task.WhenAll` database fan-out. If independent connected scopes require multiple
   calculations, execute them over one materialized snapshot with a named hard cap rather than one
   connection/transaction per row.
5. Add PostgreSQL and SQLite scale tests at the maximum page width and a broad Awaiting Progress
   fixture. Record wall-clock and command-count budgets in `docs/traceability/performance-budgets.md`.

**Acceptance:** Database round trips and concurrent connections remain constant (or a documented
small constant) as one result page widens; Awaiting Progress output is bounded; cost redaction and
totals remain unchanged.

`ICostQueryPort.GetBulkCostInputsAsync` materializes the union of every candidate's subtree inputs
(worker sessions via the same `worker_overlapping_sessions` path, schedules, rates, plus a whole-tree
node/owner map) in one provider snapshot; `CostQueries.GetBulkNodeCostsAsync` partitions each
contributing worker's sessions once (`CostSegmentPartitioner.Partition`) and runs the new
`CostEngine.ComputeLeafCosts` (the nodeId-independent half of `Calculate`) once per worker, then
`HierarchicalCostAggregator.Aggregate` — already public — once per candidate root over the same
partitioned allocations, so the expensive per-segment rate resolution runs once per worker
regardless of how many candidates are on the page. ADR 0040's ancestor-ownership gate is walked
entirely in memory from the same snapshot (no `GetAncestorOwnerIdsAsync` round trip per row).
`JobQueries.EnrichSummariesWithCostAsync`/`EnrichAwaitingProgressWithCostAsync` pre-filter candidates
with the existing ADR 0042 `CanViewNodeCost` check (unchanged) before the one bulk call; a candidate
that call excludes is simply absent from the result, matching the prior per-row redaction exactly.
The HTTP children/search endpoints now fetch exactly `pageSize` rows and only probe for another page
(`Limit = 1`, skipped when the page didn't fill) instead of enriching a `pageSize + 1`-row page and
discarding the last row. Awaiting Progress gained `Offset`/`Limit` (validated through the existing
`ValidatePaging`), sliced before enrichment, with a 50-row dashboard page and a "Next page" link.
Follow-up review made that library contract itself bounded: an omitted limit now uses
`AwaitingProgressPaging.DefaultPageSize`, and excessive limits clamp to
`AwaitingProgressPaging.MaxPageSize`, so non-web callers cannot retain the former unbounded shape.

Coverage: `CostQueriesTests`/`FakeCostQueryPort` (bulk correctness against individually-computed
hierarchy totals, ADR 0040 ancestor-ownership admission/exclusion, one-round-trip-per-call
regardless of candidate count, empty-candidate short circuit), `JobQueriesTests` (one bulk call
prices a 25-row page, `GetAwaitingProgressAsync` pages without gaps/overlap and rejects invalid
offset/limit), `CostQueryPortContractTestsBase` against both providers (bulk pricing matches
individual totals, a candidate the actor may not view is omitted without failing the rest, a
200-candidate maximum-page-width fixture completes promptly, and narrow/maximum-width calls have
identical command counts within the named 16-command budget while opening at most one connection
concurrently), the existing
`JobContextApiTests` `hasMore`/offset/pageSize coverage (unchanged behavior under the new
exactly-`pageSize`-plus-one-row-probe shape), and a new direct-HTTP test proving Awaiting Progress's
"Next page" link advances the offset correctly.

## 3. Delivery order

Every slice follows TDD: failing test first, smallest correct implementation, then refactor. Keep
unrelated findings in separate conventional commits with the required explanatory paragraph.

1. **§2.4 Complete the civil-time boundary (web).** This is a live data-correctness defect and
   extends the already-landed parser work. Cover job planning fields, rate effective periods, cost
   `AsOf`, and zoned rendering.
2. **§2.6 Make authentication audit actors unambiguous (database → library → web).** Freeze the
   schema semantics, implement both providers, then test colliding names and concurrency.
3. **§2.3 Bound audit history (Application → PostgreSQL/SQLite → web).** Freeze paging shape before
   editing public contracts; implement provider-side keyset limiting and UI continuation.
4. **§2.8 Remove row-proportional cost fan-out (Application → providers → web/API).** Add the bulk
   contract and bounded Awaiting Progress result before scale verification.
5. **§2.5 Introduce the accepted clock seam (cross-cutting).** Land as small layer-scoped commits,
   keeping both providers conformant at each step; finish with the architecture guard.
6. **§2.7 Make PAT delivery PRG-safe (web/credential lifecycle).** Introduce the bounded one-use
   reservation/store behavior, then replace the knowingly stale test expectation.

The order keeps database contract decisions ahead of dependent library/web work. §2.5 is
cross-cutting but should not be mixed into functional fixes; use the existing public factory seam
to migrate one vertical area at a time.

## 4. Verification gate

For every commit, run the normal scoped gate from `JobTrack/` with unsandboxed `dotnet` calls:

```bash
gtimeout 300 dotnet build JobTrack.slnx -warnaserror
dotnet format JobTrack.slnx
dotnet format JobTrack.slnx --verify-no-changes
gtimeout 300 ./scripts/fast-test.sh --build
gtimeout <category-budget> dotnet test <affected-project> --filter "FullyQualifiedName~AffectedClass"
```

Run the full solution suite once after the plan is complete because the work changes both providers,
public contracts, Identity/security semantics, and the web host. Also run:

- the PostgreSQL/SQLite authentication-audit concurrency contracts after §2.6;
- web integration and end-to-end axe tests after §2.4 and §2.7;
- public API approval after §2.3, §2.5, §2.6, and §2.8;
- provider scale/command-count tests after §2.8; and
- `./scripts/clean-test-databases.sh` after any interrupted database run.

The plan is complete only when:

- every Razor instant is parsed and rendered in the viewing employee's explicit zone;
- runtime time reads use one injected/captured `IClock` instant per operation;
- unknown authentication subjects cannot collide with or impersonate an employee in audit history;
- audit and Awaiting Progress result materialization is bounded;
- list cost enrichment has no per-row database fan-out;
- PAT issuance is PRG-safe without persisting plaintext in browser state; and
- the plan status, plans index, traceability catalogue, and affected operational/performance docs
  agree with the delivered evidence.
