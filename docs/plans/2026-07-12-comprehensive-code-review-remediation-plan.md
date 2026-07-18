# Comprehensive Code Review Remediation Plan

**Date:** 2026-07-12
**Status:** In progress — §2.1–§2.15 implemented 2026-07-12 (commits on `main`); §3 delegated plans unchanged.
**Scope:** Full-stack fresh-eyes review of JobTrack after M3/M6/M8 gate acceptance (phases 1–3),
the external HTTP API/PAT work, and the client requester intake slice. This plan records **new**
findings from the 2026-07-12 review. It does not restate work already tracked in other open plans
(see §3).

**Review method:** Build and fast core suite (`dotnet build JobTrack.slnx -warnaserror`,
`./scripts/fast-test.sh`) both pass at review time. Findings were validated against source,
architecture tests, ADRs, and impl-plan §14 review prompts.

---

## 1. Review Baseline — What Is Already Strong

The project is in good architectural shape for a greenfield system at this milestone:

- **Layering is enforced.** Web and HTTP API call `IJobTrackClient` only; architecture tests block
  persistence namespaces and ad hoc SQL outside composition. The ExternalApiClient sample has zero
  `JobTrack.*` assembly references (ADR 0029/0030 proof intent).
- **House style is largely followed in the domain core.** Immutable records, Noda Time inside the
  domain, rational allocated-time shares, `decimal` money paths, no `double` on duration/money,
  exhaustive `Achievement` switches, and exceptions (not `Result`) as the failure channel.
- **Database contracts are serious.** Dual-provider schema scripts, GiST/trigger overlap enforcement,
  stored functions for irreducible PostgreSQL concurrency (`move_job_node`, `add_job_prerequisite`,
  `worker_overlapping_sessions`), shared contract tests, and provider race suites.
- **Security posture is above average.** Strict cookie flags, CSRF on cookie-backed API writes,
  bearer PAT auth with hashed storage, rate limits, bounded API telemetry, CSP and security headers,
  cost-trace foreign-session filtering (ADR 0017), and PostgreSQL role separation.
- **Test discipline is real.** TDD patterns, AwesomeAssertions, contract-test bases per port,
  OpenAPI route-set tests, integration tests for bearer/cookie auth boundaries, and axe WCAG scans
  in end-to-end tests.

The findings below are gaps against spec/ADR intent, defense-in-depth weaknesses, or hardening debt —
not a fundamental redesign.

---

## 2. Findings

Severity: **Critical** > **High** > **Medium** > **Low**. Categories map to impl-plan §14 layers.

### 2.1 Forced password change applies to Razor Pages only — `/api/*` remains usable

| | |
|---|---|
| **Severity** | **High** |
| **Category** | Interface / auth (§14.3) |
| **Files** | `src/JobTrack.Web/RequiresPasswordChangePageFilter.cs`, `src/JobTrack.Web/JobTrackApi.cs`, `src/JobTrack.Web/Program.cs`, `src/JobTrack.Persistence.Shared/ActorAccountState.cs` |

Spec §7.1 / §8.1: a user with a temporary password must choose a new password before anything else
is available. `RequiresPasswordChangePageFilter` enforces this for Razor Pages only. A user who signs
in with a temporary password receives a valid session cookie and can call any authorized `/api/*`
endpoint (including `GET /api/antiforgery-token` and job/cost reads) while the page filter redirects
browser navigation to change-password. `ActorAccountState.EnsureMayAct` checks disabled/lockout but
not `RequiresPasswordChange`.

**Remediation:**

- Add an endpoint filter (or post-authentication middleware) on the `/api` route group that rejects
  requests when the authenticated identity user has `RequiresPasswordChange`, with the same exempt
  paths as the page filter (`/Account/ChangePassword`, logout).
- Defense in depth: reject bearer PAT auth in `PersonalAccessTokenAuthenticationHandler` when the
  account flag is set (admin reset already revokes PATs).
- Integration test: user with `requires_password_change = 1` and valid cookie receives `403` (or a
  dedicated problem type) from `GET /api/jobs/root`.

---

### 2.2 Query handlers load sensitive data before authorization checks

| | |
|---|---|
| **Severity** | **High** |
| **Category** | Library API / security (§14.2) |
| **Files** | `src/JobTrack.Application/CostQueries.cs` (lines 111–115), `JobQueries.cs` (multiple handlers), `AuditQueries.cs` |

Cost, job browse, employee profile, session list, schedule, and rate query handlers call persistence
ports first, materializing full payloads (including ADR 0017 elevated-scope worker sessions and rates
for cost calculation), then call domain `*AccessPolicy` and throw `AuthorizationDeniedException`.
Unauthorized actors still trigger expensive reads and hold cross-scope data in memory before denial.

**Remediation:**

- For cost queries: resolve actor roles and call `CostAccessPolicy.CanView` **before**
  `GetCostInputsAsync`, or split the port into cheap auth metadata + heavy inputs.
- Apply the same pattern to other query handlers where port reads are expensive or carry foreign
  scope (employee profiles, audit payloads).
- Add Application-layer tests proving unauthorized actors do not invoke heavy port methods (fake port
  records call order).

---

### 2.3 Request acknowledgment is not “set once” (ADR 0034 violation)

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Database / library (§14.1) |
| **Files** | `PostgreSqlJobRequestCommandPort.AcknowledgeAsync`, `SqliteJobRequestCommandPort.AcknowledgeAsync`, `database/{postgresql,sqlite}/schema-versions/*_department-and-request-holding-area.sql`, `docs/decisions/0034-requester-acceptance-and-notes.md` |

ADR 0034 and `IRequestCommands` document acknowledgment as a one-time staff action. `AcknowledgeAsync`
unconditionally sets `AcknowledgedAt` / `AcknowledgedByUserId` after a version check, with no guard
that they are still null. A second call with a fresh version overwrites the first acknowledgment.
No schema trigger enforces immutability (unlike `job_request_note`).

**Remediation:**

- Application: reject when `AcknowledgedAt is not null` with a stable invariant
  (e.g. `request-already-acknowledged`), or use conditional update
  `WHERE acknowledged_at IS NULL AND row_version = @version` (pickup CAS pattern).
- Schema (both providers): add trigger or CHECK preventing update once set; add
  `CHECK ((acknowledged_at IS NULL) = (acknowledged_by_user_id IS NULL))` for column pairing.
- Contract tests: second acknowledge with fresh version must fail on both providers.

---

### 2.4 SQLite read-only query ports skip required connection pragmas

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Persistence / operations |
| **Files** | All `Sqlite*QueryPort.cs` under `src/JobTrack.Persistence.Sqlite/` (12 read ports); contrast `Sqlite*CommandPort.cs` which call `SqliteConnectionPragmas.ConfigureConnectionSql` |

Write-side SQLite ports configure `foreign_keys`, `busy_timeout`, `journal_mode`, and `synchronous`
on every connection. Read-only query ports open contexts with bare `UseSqlite(connectionString)` and
never issue pragmas. Under concurrent read/write on the same SQLite file, readers can hit immediate
`SQLITE_BUSY` instead of waiting. This violates the project rule that every SQLite connection sets
these explicitly.

**Remediation:**

- Centralize SQLite context creation in one helper (or EF interceptor mirroring
  `JobTrack.Identity`'s pattern) and use it from **every** port — read and write.
- Extend contract or integration tests that exercise concurrent read during write on SQLite.
- Also apply the same pragma bundle in `JobTrack.Database/Program.cs` deploy connections (currently
  WAL only).

---

### 2.5 CSP `style-src 'self'` conflicts with inline `style=` attributes in Razor markup

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Interface / security (§14.3) |
| **Files** | `src/JobTrack.Web/Program.cs` (CSP), `Pages/Jobs/Browse.cshtml` (10 inline styles), `Pages/Jobs/Work.cshtml`, `Pages/Requests/Details.cshtml`, `Pages/Error.cshtml`, `docs/design-language.md` |

CSP is strict (`style-src 'self'`, no `'unsafe-inline'`). Four pages use inline `style=` attributes
(filter panels, tree indentation, error notices). Under CSP2+, inline style attributes are blocked
unless `'unsafe-inline'` or hashes are used — styling is silently dropped or the documented XSS
policy is weaker than claimed. This also violates the design language rule against hard-coded values
in markup.

**Remediation:**

- Move inline styling into `site.css` primitives (e.g. `.jt-filter-card`, depth-based tree indent
  utilities, `.jt-notice--error`).
- Do **not** add `'unsafe-inline'` to CSP.
- Re-run axe end-to-end scans after changes.

---

### 2.6 Mutation authorization is entirely delegated to persistence ports

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Library API / architecture (§14.2) |
| **Files** | `JobCommands.cs`, `WorkCommands.cs`, `RateCommands.cs`, `ScheduleCommands.cs`, `EmployeeCommands.cs`, `RequestCommands.cs`, `TokenCommands.cs`; port contracts under `Ports/` |

Application command handlers delegate to ports without Application-layer `*AccessPolicy` pre-checks.
Authorization lives exclusively in port implementations. A missing or incorrect port check is a
direct bypass with no Application safety net. This is a deliberate layering choice in some slices,
but it is undocumented and untested as a global invariant.

**Remediation:**

- **Decision:** either document “ports own mutation authorization; Application is orchestration
  only” in `docs/api/jobtrack-client-design.md` and add architecture/contract tests asserting every
  port method invokes the documented policy, **or** add lightweight Application pre-checks where
  request context already carries sufficient facts.
- Extend persistence contract test bases to cover the full authorization matrix per port category.

---

### 2.7 `GetActiveSessionsAsync` hardcodes `isOwnSession: true`

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Library API / security (§14.2) |
| **Files** | `src/JobTrack.Application/JobQueries.cs` (lines 227–232) |

The auth check always passes `isOwnSession: true` to `WorkSessionAccessPolicy.CanView`, so any actor
with `Worker` role passes regardless of session ownership. Correctness depends entirely on the port
never returning another user's sessions.

**Remediation:**

- Verify each returned session's `WorkedByUserId == request.Context.Actor`, or pass the actual
  ownership fact into `CanView`.
- Application test: fake port returning foreign sessions must be rejected.

---

### 2.8 Non-exhaustive handling of `ScheduleExceptionEffect`

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Domain / house style (§14.1) |
| **Files** | `ScheduleExceptionResolver.cs`, `ScheduleExceptionValidator.cs`, `RateResolver.cs` |

Closed enum filtered via `== AddWorkingTime` / `== RemoveWorkingTime` instead of exhaustive `switch`
with `_ => throw`. Adding a third effect would silently drop exceptions from resolution/validation —
violates house style “no silent default fallthrough.”

**Remediation:**

- Replace with exhaustive switch expressions (same pattern as `AchievementTransitions.cs`).
- Domain unit test that an unknown enum value (via reflection test helper if needed) throws.

---

### 2.9 Incomplete hierarchy inputs throw `KeyNotFoundException`

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Domain / error handling (§14.1) |
| **Files** | `AchievementCalculator.cs`, `ReadinessCalculator.cs`, `RateResolver.cs`, `CostSegmentPartitioner.cs`, `HierarchicalCostAggregator.cs`, `AwaitingProgressCalculator.cs` |

Pure domain functions index `nodesById[id]` without guard. Incomplete graphs produce BCL
`KeyNotFoundException` rather than `InvariantViolationException` from the JobTrack hierarchy.
Callers cannot distinguish bad caller input from missing entity.

**Remediation:**

- Centralize lookup helper throwing `InvariantViolationException("hierarchy.missing-node", ...)`, or
  validate graph completeness at port materialization boundary before calling domain calculators.

---

### 2.10 `CostEngine` hardcodes `IsWorkingTime: true` in trace output

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Domain / spec alignment (§14.1) |
| **Files** | `src/JobTrack.Domain/Costing/CostEngine.cs`; exposed via `CostSegmentTrace` and HTTP API |

Impl plan §6 calls for segment traces recording working-time eligibility. Every trace entry is
stamped `true` without computation. Totals are unaffected today (segments come from working-time
clipping), but the field is non-informative and would be wrong if non-working segments enter the
trace path.

**Remediation:**

- Compute eligibility from schedule/exception context during partitioning, **or** remove the field
  from the public trace contract until it carries real semantics (requires ADR/spec note if removed).
- Add a domain test asserting false for a non-working segment scenario.

---

### 2.11 Employee dropdowns enumerate all identity users without role/state filter

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | Interface / information disclosure (§14.3) |
| **Files** | `Pages/Jobs/Work.cshtml.cs`, `Pages/Jobs/AwaitingProgress.cshtml.cs` |

Both pages query **all** `identity_user` rows for owner/worked-by dropdowns — including disabled
accounts and non-workflow roles (Requester, Auditor). This bypasses `IJobTrackClient` for identity
enumeration and exposes usernames the workflow layer never intended to surface.

**Remediation:**

- Filter to enabled employees with workflow-relevant roles via `UserManager`/store query, or add a
  narrow read on `IJobTrackClient` if a list-employees query is preferred.
- At minimum filter `IsEnabled == true`.

---

### 2.12 Architecture tests do not cover several documented web/sample invariants

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | Test / architecture |
| **Files** | `tests/JobTrack.ArchitectureTests/WebHostCompositionBoundaryTests.cs`, `ReusableLibraryDependencyTests.cs` |

Existing tests correctly block persistence in web pages and enforce library dependency rules. Gaps:

- No assertion that `samples/JobTrack.ExternalApiClient` has zero `JobTrack.*` references.
- No scan that every Razor `PageModel` (except allowlisted public pages) has `[Authorize]`.
- No scan that every mapped `/api/*` endpoint has `RequireAuthorization`.
- No guard against new direct `JobTrackIdentityDbContext` usage outside composition/identity adapter.

**Remediation:**

- Add focused architecture tests with explicit allowlists for anonymous pages and identity DbContext
  usage sites.

---

### 2.13 Missing Application-layer tests for `RequestCommands`

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | Test coverage (§14.2) |
| **Files** | `src/JobTrack.Application/RequestCommands.cs`; no counterpart in `tests/JobTrack.Application.Tests/` |

Every other command group has Application tests with fake ports. `RequestCommands` coverage relies on
persistence contract and web integration tests only.

**Remediation:**

- Add `RequestCommandsTests` with a fake `IJobRequestCommandPort` mirroring existing patterns.

---

### 2.14 `SqliteStrictAndWithoutRowidSchemaTests` table inventory is stale

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | Database / test coverage (§14.1) |
| **Files** | `tests/JobTrack.Database.ContractTests/SqliteStrictAndWithoutRowidSchemaTests.cs` vs schema versions 0013–0014 |

`AllTableNames` omits `personal_access_token`, `department`, `app_user_department`,
`request_holding_area`, `job_request`, and `job_request_note`. `WithoutRowidTableNames` omits
`app_user_department` and `job_request`. New tables may ship without STRICT/WITHOUT ROWID regression
coverage.

**Remediation:**

- Extend both lists to match deployed SQLite schema.

---

### 2.15 Minor library hygiene items

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | House style / public API |
| **Files** | See below |

| Item | Files | Fix |
|------|-------|-----|
| Two public types per file | `EquatableDictionary.cs`, `EquatableArray.cs` | Split factory helpers to separate files |
| Unused `NodaTime` package reference | `JobTrack.Abstractions.csproj` | Remove unless planned |
| Mojibake in `IJobTrackClient` XML docs | `IJobTrackClient.cs` (lines 4–8) | Re-save UTF-8; restore `§` symbols |
| Empty inline `<script type="importmap">` | `Pages/Shared/_Layout.cshtml` | Remove unused tag (CSP blocks inline scripts) |
| Stale PAT issuance doc | `docs/api/external-http-api-reference.md` | Document `/Account/PersonalAccessTokens` Razor surface |
| Public persistence port interfaces | `JobTrack.Application/Ports/*.cs` | Consider `internal` + `InternalsVisibleTo` or document as extension surface |

---

## 3. Findings Delegated to Existing Open Plans

Do **not** duplicate remediation detail here — track and close in the cited plan:

| Topic | Plan | Status at review |
|-------|------|------------------|
| External API pagination, OpenAPI contract depth, bearer problem-details normalization, authorization boundary evidence | `2026-07-10-external-http-api-remediation-plan.md` | Proposed |
| TZDB version persistence, zone-id rot handling, zone canonicalisation, LocalTime whole-second guard | `2026-07-12-temporal-representation-hardening-plan.md` | Accepted (decisions resolved; implementation pending) |
| Nullable owner, unassigned pool, owner-gated work authorization | `2026-07-11-job-node-ownership-and-work-authorization.md` | Proposed — not started |
| PostgreSQL range columns, `ltree` paths, case-insensitive identity uniqueness | `2026-07-11-postgresql-column-type-remediation-plan.md` | Proposed |
| Client requester intake (submission, triage, move, UI/API) | `2026-07-11-client-requester-intake-plan.md` | Core slice complete |
| Phase-gate evidence packaging | `2026-07-09-phase-gate-evidence-plan.md` | Proposed |

**Already closed (no action in this plan):**

- `2026-07-11-security-review-remediation-plan.md` — all seven findings remediated.
- `2026-07-08-fix-plan.md` — closed.
- `2026-07-09-overlapping-cost-scale-plan.md` — implemented.

---

## 4. Implementation Order

Use TDD for each item. Respect mandatory layer order: database defect → library → web/API. Run the
commit gate per slice (`dotnet build … -warnaserror`, `dotnet format … --verify-no-changes`,
`./scripts/fast-test.sh --build`, targeted `--filter`). Full solution suite once at plan completion.

### Stage 1 — Security and auth choke points (web + library)

1. **§2.1** Forced password change on `/api/*` — integration test first, then endpoint filter +
   optional PAT defense.
2. **§2.2** Authorization-before-load for cost queries (highest sensitivity) — fake-port call-order
   test, then reorder checks; extend to other expensive query handlers.

### Stage 2 — Request acknowledgment invariants (database → library)

3. **§2.3** Failing contract test for re-acknowledge; application guard; schema CHECK/trigger on
   both providers; column-pairing CHECK.

### Stage 3 — SQLite operational correctness (persistence)

4. **§2.4** Centralize pragma setup; migrate all read ports; fix deployer connection; concurrent
   read/write SQLite test.

### Stage 4 — Domain and Application hardening (library)

5. **§2.8** Exhaustive `ScheduleExceptionEffect` switches.
6. **§2.9** Hierarchy lookup helper / boundary validation.
7. **§2.7** `GetActiveSessionsAsync` ownership verification + Application test.
8. **§2.6** Document port-owned mutation auth **or** add Application pre-checks + contract tests.
9. **§2.10** `IsWorkingTime` trace semantics — compute or remove with ADR note.
10. **§2.13** `RequestCommandsTests`.

### Stage 5 — Web UI and architecture guardrails (interface)

11. **§2.5** Move inline styles to `site.css`; axe e2e.
12. **§2.11** Filter employee dropdowns.
13. **§2.12** Architecture test extensions.
14. **§2.14** Update SQLite STRICT/WITHOUT ROWID inventory.
15. **§2.15** Library/doc hygiene (UTF-8 docs, unused package, importmap removal, external API doc).

### Stage 6 — Execute delegated plans (parallel tracks, dependency-ordered)

16. Temporal hardening plan slices 1–4 (§3).
17. External HTTP API remediation plan (§3).
18. Job-node ownership plan when product confirms model §8 assumptions (§3).
19. PostgreSQL column-type plan when performance evidence warrants (§3).

---

## 5. Completion Criteria

This comprehensive review is remediated when:

- **§2.1–§2.15** findings are closed with tests cited in `docs/traceability/test-catalogue.md`;
- delegated plans in §3 reach their own completion criteria without leaving overlapping debt in
  this plan;
- `./scripts/fast-test.sh --build` and targeted provider/web filters for touched areas pass under
  `gtimeout`; and
- a full `dotnet test JobTrack.slnx` run passes once at plan completion.

---

## 6. Risk Summary

| Priority | Finding | Risk if deferred |
|----------|---------|------------------|
| 1 | §2.1 API bypass during forced password change | Spec violation; temporary-password accounts retain full API access |
| 2 | §2.2 Auth-after-load on cost queries | Information exposure to unauthorized actors; unnecessary load |
| 3 | §2.3 Re-acknowledge | Audit integrity; ADR 0034 semantic violation |
| 4 | §2.4 SQLite read pragmas | Production SQLite flakiness under concurrency |
| 5 | §2.5 CSP vs inline styles | XSS policy weaker than documented; broken UI styling |
| 6 | §3 temporal plan | ADR 0008 reproducibility guarantee unmet |
| 7 | §3 external API plan | Unbounded collections; incomplete contract evidence |
| 8 | §3 ownership plan | Any Worker can record time on any node (known product gap) |

---

## 7. Review Prompt Coverage (impl plan §14)

| Prompt area | Verdict |
|-------------|---------|
| **§14.1 Database/domain** | Strong invariant enforcement; gaps in request acknowledgment immutability, SQLite read pragmas, temporal plan items, and domain error typing for incomplete graphs |
| **§14.2 Library API** | Clean facade and immutability; gaps in auth ordering, port-owned mutation auth documentation, and missing Application tests for requests |
| **§14.3 Interface** | Good CSRF/CSP/rate-limit baseline; gaps in forced password change API enforcement, CSP/markup alignment, and identity enumeration in dropdowns |
