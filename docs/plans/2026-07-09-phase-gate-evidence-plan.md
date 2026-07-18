# JobTrack Phase 1-3 Gate Evidence Plan

**Date:** 2026-07-09  
**Scope:** Close evidence and traceability gaps found after assessing `docs/plans/jobtrack_impl_plan.md`
Phases 1-3.  
**Status:** Implemented. ADR 0025 (M3 database gate), ADR 0026 (M6 library gate), and ADR 0027
(M8 web gate) are all recorded with `Status: Accepted`. The traceability rows listed in §3.1 (lines
98-107 at the time this plan was written) are filled in with fully qualified test IDs in
`docs/traceability/test-catalogue.md`. Gate ordering (M3 before M6 before M8) holds via each ADR's
own acceptance date. Remaining `pending` rows in the traceability catalogue as of 2026-07-12 belong
to the requester-intake staff triage slice (ADR 0033/0034), tracked separately by
`docs/plans/2026-07-12-end-user-testing-readiness-remediation-plan.md` §2.1 — not a Phase 1-3 gate
gap.

## 1. Objective

Make the Phase 1 database gate, Phase 2 reusable-library gate, and Phase 3 web gate auditable from
source-controlled evidence.

The implementation currently builds, formats, packages, and passes the full automated test suite,
including database contract/performance tests, provider conformance tests, web integration tests,
and browser end-to-end tests. The remaining problem is not broad implementation absence; it is that
the plan's formal gate evidence is incomplete or stale:

- `docs/traceability/test-catalogue.md` still marks several web/security rows as `pending`.
- There is formal M0 acceptance, but no equivalent M3 database gate, M6 library gate, or M8 web gate
  acceptance record.
- Mutation-test evidence is documented but must be refreshed or explicitly revalidated against the
  current code.
- Web/security evidence exists in code for several rows but is not consistently mapped back to the
  traceability catalogue.

This plan closes those issues without weakening existing tests or adding domain/persistence logic to
the web layer.

## 2. Current Baseline

Last assessment run on 2026-07-09:

```bash
gtimeout 300 dotnet build JobTrack.slnx -warnaserror
gtimeout 120 dotnet format JobTrack.slnx --verify-no-changes
gtimeout 600 dotnet test JobTrack.slnx --no-build
gtimeout 300 dotnet list package --vulnerable --include-transitive
```

Results:

- Build passed with 0 warnings.
- Format verification passed.
- Full test suite passed: 1,373 tests, 0 failed, 0 skipped.
- Vulnerability check reported no vulnerable packages across all projects.
- The six packable reusable-library projects produced both `.nupkg` and `.snupkg` packages.

Use this as the starting point. If any command fails while implementing this plan, fix the regression
instead of lowering a test or gate.

## 3. Work Order

### 3.1 Reconcile the traceability catalogue

**Finding:** `docs/traceability/test-catalogue.md` lines 98-107 still contain `pending` or partial
rows for web/security and database credential-compromise evidence.

**Work:**

- For each pending row, inspect existing implementation and tests before writing new tests.
- Replace `pending` with fully qualified test method names where adequate coverage already exists.
- Where coverage is partial, add a failing test first and then the smallest implementation change
  needed to satisfy it.
- Keep traceability rows stable; do not renumber or remove test-case identifiers.
- Link each new or newly-mapped test back to the relevant `TC-*` identifier where the connection is
  not obvious from the class name.

**Rows to close:**

- `TC-WEB-AUTHN-005`: cookie attributes plus security-stamp revocation on disablement, reset, and
  password change.
- `TC-WEB-AUTHN-007`: CSP/output-encoding XSS evidence using injected script content.
- `TC-WEB-AUTHZ-001`: crafted direct-request authorization bypass coverage.
- `TC-WEB-AUTHZ-002`: IDOR coverage for opaque identifiers and authorization after entity
  resolution.
- `TC-WEB-AUTHZ-003`: subtree-scope confusion coverage.
- `TC-WEB-AUTHZ-004`: mass-assignment coverage for command-specific binding models.
- `TC-WEB-AUDIT-002`: sensitive logging/audit evidence for secrets, rate data, and cost data.
- `TC-DB-ROLES-002`: database credential-compromise coverage proving the application role cannot
  read Identity secret columns directly.
- `TC-WEB-IDENT-003`: emergency reset abuse coverage, including forced change, revocation, scoped
  privilege, and secret-free audit.

**Acceptance checks:**

- `rg -n "pending|partial:" docs/traceability/test-catalogue.md` returns no unresolved Phase 1-3
  gate rows.
- Every row in the list above maps to passing tests or explicitly documented accepted residual risk.
- No test is weakened, skipped, or deleted.

### 3.2 Add formal M3 database gate acceptance

**Finding:** Phase 1 has strong database artifacts and passing tests, but no source-controlled
acceptance record equivalent to `docs/decisions/0020-m0-acceptance.md`.

**Work:**

- Add `docs/decisions/0025-m3-database-gate-acceptance.md`.
- Record the exact evidence for every `jobtrack_impl_plan.md` §6.7 bullet:
  empty/upgrade deployment, invariant enforcement and bypass tests, provider-equivalent query
  fixtures, race tests, PostgreSQL query-plan/scale budgets, SQLite limitations documentation,
  role grants, and backup/restore smoke evidence.
- Reference the concrete test projects/classes and operational documents rather than restating the
  whole database design.
- If any §6.7 bullet lacks credible evidence, stop and implement the missing test/evidence before
  marking M3 accepted.

**Acceptance checks:**

- The ADR exists and has status `Accepted`.
- Each §6.7 bullet has a concrete evidence pointer.
- `JobTrack.Database.ContractTests`, `JobTrack.Database.PerformanceTests`,
  `JobTrack.Persistence.PostgreSql.Tests`, and `JobTrack.Persistence.Sqlite.Tests` pass under
  `gtimeout`.

### 3.3 Refresh M6 library gate evidence and acceptance

**Finding:** Phase 2 has passing API, architecture, domain, application, provider, sample, package,
and vulnerability evidence. Mutation testing is documented, but the current code should be
revalidated before accepting M6.

**Work:**

- Re-run the scoped Stryker gate from `src/JobTrack.Domain` using the documented config.
- Update `docs/operations/mutation-testing-gate.md` with the new run timestamp, score, and any
  changed survivor triage.
- Add `docs/decisions/0026-m6-library-gate-acceptance.md`.
- Record evidence for every `jobtrack_impl_plan.md` §7.5 bullet:
  public API baselines, architecture tests, unit/property tests, provider conformance suites,
  mutation testing, sample consumers, cancellation/timeout/disposal/retry/telemetry evidence,
  package metadata, vulnerability checks, and absence of ASP.NET Core dependencies.
- Treat any new non-equivalent mutation survivor as a test gap and close it with TDD.

**Acceptance checks:**

- Stryker passes the configured break threshold.
- The mutation-gate document reflects the current run, not only the historical 2026-07-07 run.
- The M6 ADR exists and has status `Accepted`.
- Library-relevant test projects and package/vulnerability checks pass under `gtimeout`.

### 3.4 Add formal M8 web gate acceptance

**Finding:** Phase 3 has broad web evidence, but the web gate cannot be cleanly accepted while
traceability rows remain stale and no M8 acceptance record exists.

**Work:**

- Complete §3.1 first; do not accept M8 with unresolved traceability rows.
- Add `docs/decisions/0027-m8-web-gate-acceptance.md`.
- Record evidence for every `jobtrack_impl_plan.md` §8.7 bullet:
  authentication/revocation/lockout/antiforgery/security headers/enumeration, explicit endpoint
  policies and direct-request negatives, role/ownership/session/schedule/cost visibility, problem
  details, PostgreSQL and SQLite E2E scenarios, responsive/browser/accessibility coverage,
  dependency/security scans, and no web SQL/provider leakage outside composition.
- Include references to `docs/operations/web-host-security.md`,
  `docs/operations/browser-testing.md`, and the relevant integration/E2E test classes.
- If a §8.7 item is genuinely deferred to Phase 4/release hardening, record that as explicit
  residual risk rather than silently calling the gate complete.

**Acceptance checks:**

- The M8 ADR exists and has status `Accepted`.
- `JobTrack.Web.IntegrationTests`, `JobTrack.Web.EndToEndTests`, `JobTrack.Identity.Tests`, and
  `JobTrack.AdminCli.Tests` pass under `gtimeout`.
- Architecture tests still prove that web contains no SQL and uses provider implementations only in
  the composition root.

### 3.5 Final gate audit

**Finding:** Passing tests alone are not enough; the plan requires auditable gate evidence and
ordered acceptance.

**Work:**

- Add a short "Phase 1-3 gate status" section to `docs/plans/jobtrack_impl_plan.md` or add a
  dedicated traceability note that points to ADRs 0025-0027.
- Verify the gate ordering is explicit: M3 accepted before M6, M6 accepted before M8.
- Re-run the full commit gate.
- Check that generated package output and Stryker reports are not accidentally staged.

**Acceptance checks:**

- `docs/decisions/0025-m3-database-gate-acceptance.md`,
  `docs/decisions/0026-m6-library-gate-acceptance.md`, and
  `docs/decisions/0027-m8-web-gate-acceptance.md` exist and are internally consistent.
- No `pending` Phase 1-3 traceability rows remain.
- The full verification gate in §4 passes.

## 4. Verification Gate

Run every command through `gtimeout` and run `dotnet` unsandboxed in this repository.

```bash
gtimeout 300 dotnet build JobTrack.slnx -warnaserror
gtimeout 120 dotnet format JobTrack.slnx --verify-no-changes
gtimeout 600 dotnet test JobTrack.slnx --no-build
gtimeout 300 dotnet list package --vulnerable --include-transitive
```

Package gate:

```bash
rm -rf /tmp/jobtrack-pack-assessment
mkdir -p /tmp/jobtrack-pack-assessment
for p in JobTrack.Abstractions JobTrack.Domain JobTrack.Application JobTrack.Persistence.Shared JobTrack.Persistence.PostgreSql JobTrack.Persistence.Sqlite; do
  dotnet pack src/$p/$p.csproj -c Release -o /tmp/jobtrack-pack-assessment
done
```

Mutation gate:

```bash
cd src/JobTrack.Domain
gtimeout 1800 dotnet-stryker --config-file stryker-config.json
```

Targeted suites, useful while implementing:

```bash
gtimeout 300 dotnet test tests/JobTrack.Database.ContractTests/JobTrack.Database.ContractTests.csproj
gtimeout 300 dotnet test tests/JobTrack.Database.PerformanceTests/JobTrack.Database.PerformanceTests.csproj
gtimeout 300 dotnet test tests/JobTrack.Persistence.PostgreSql.Tests/JobTrack.Persistence.PostgreSql.Tests.csproj
gtimeout 180 dotnet test tests/JobTrack.Persistence.Sqlite.Tests/JobTrack.Persistence.Sqlite.Tests.csproj
gtimeout 180 dotnet test tests/JobTrack.Web.IntegrationTests/JobTrack.Web.IntegrationTests.csproj
gtimeout 600 dotnet test tests/JobTrack.Web.EndToEndTests/JobTrack.Web.EndToEndTests.csproj
```

## 5. Non-Goals

- Do not redesign database, library, or web workflows that already satisfy the plan.
- Do not weaken, skip, delete, or simplify tests to close traceability rows.
- Do not add web-layer SQL, provider-specific domain behaviour, or duplicate authorization/costing
  rules in Razor Pages.
- Do not convert Phase 4 release-hardening work into a prerequisite for M8 unless §8.7 explicitly
  requires it.
- Do not push to git; commits only when requested.
