# ADR 0027: M8 (web) gate acceptance

**Status:** Accepted
**Closes:** Implementation plan §8.7

## Decision

M8 is formally accepted. Every §8.7 web-gate exit criterion is satisfied. This ADR presumes §3.1 of
`docs/plans/2026-07-09-phase-gate-evidence-plan.md` is already closed — no traceability row feeding
this gate is `pending` or `partial`.

| Criterion | Evidence |
|---|---|
| Authentication, revocation, lockout, antiforgery, security headers, and enumeration tests pass | `JobTrack.Web.IntegrationTests.AccountFlowTests` (sign-in, lockout, enumeration, antiforgery, forced password change, logout), `AdminAccountManagementTests` (disablement and reset revocation), `ChangePasswordTests` (self-service password-change revocation), `SecurityHeadersTests` (CSP and related headers), `LoginRateLimitConfigurationTests`, `DisabledAccountSignInTests` |
| Every endpoint has an explicit authorization policy and direct-request negative tests | Every Razor Page/API endpoint in `src/JobTrack.Web` carries an explicit `[Authorize(Policy = ...)]` (`Program.cs`'s named policies); direct-request negatives closed by `AdminRoleAssignmentTests.An_authenticated_non_administrator_is_denied` (`TC-WEB-AUTHZ-001`), `EditJobNodeTests`/`MoveJobNodeTests.A_worker_who_does_not_own_the_node_is_denied_at_confirm` (`TC-WEB-AUTHZ-002`), and the subtree-scope-confusion case in `EditJobNodeTests.A_worker_who_owns_subtree_a_is_denied_at_confirm_for_a_node_in_sibling_subtree_b` (`TC-WEB-AUTHZ-003`) |
| Role combinations, ownership boundaries, own-session/schedule rules, and independent cost visibility pass | `RateAdministrationTests`, `ScheduleTests`, `CostReportTests` (`A_worker_cannot_open_the_cost_report`), `AuditBrowsingTests` (rate-event redaction by role), `AdminRoleAssignmentTests.An_unexpected_extra_form_field_has_no_effect_beyond_the_allow_listed_fields` (`TC-WEB-AUTHZ-004`, mass-assignment) |
| Problem details disclose no internal or sensitive information | `HttpApiTests.An_overlapping_rate_returns_problem_details_without_provider_leakage` and the API's other domain-exception-to-`ProblemDetails` mappings in `JobTrackApi.cs`; `SensitiveLoggingTests` proves no auth secret reaches logs/exceptions either (`TC-WEB-AUDIT-002`) |
| End-to-end tests cover the complete operational scenarios in both PostgreSQL and SQLite configurations | `JobTrack.Web.EndToEndTests` — ten `*BrowserTestsBase` classes (sign-in/browse, job-node structure, leaf work sessions, prerequisites/achievement, schedule, rate administration, cost reports, audit browsing, admin account management) each run against both `SqliteBrowserFixture` and `PostgreSqlBrowserFixture`; `ProviderSmokeTests` additionally exercises both providers end to end |
| Responsive browser tests pass at the agreed phone, tablet, and desktop viewports without unintended overflow, clipped actions, or desktop-only workflows | `docs/operations/browser-testing.md` "Viewport matrix" (small phone/large phone/tablet/desktop) and "400% zoom / text-resize evidence" (320px reflow, WCAG 1.4.10 equivalent) |
| Accessibility, keyboard navigation, browser compatibility, performance, and dependency/security scans meet agreed budgets | `docs/operations/browser-testing.md` "Coverage today" (automated accessibility scan + keyboard/focus checks on every slice, every provider); `CrossBrowserCompatibilityTests` (Firefox, WebKit); `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages across the solution (2026-07-09 run) |
| The web project has no direct reference to provider implementation APIs beyond composition registration and no SQL | `JobTrack.ArchitectureTests.WebHostCompositionBoundaryTests.No_file_outside_the_composition_root_references_a_persistence_provider_namespace` and `.No_file_contains_direct_SQL` |

Supporting operational documentation: `docs/operations/web-host-security.md` (production configuration
requirements not provable inside `WebApplicationFactory`) and `docs/operations/browser-testing.md`
(tool choice, viewport matrix, coverage matrix) are referenced rather than restated here.

## Consequences

- Per plan §1, §8.7, and the mandatory implementation order in `CLAUDE.md`, Phase 4 production
  hardening and release work (§9) may now proceed. No web-layer workaround compensates for a gap in
  an earlier phase — a defect traced back to a database or library decision is fixed there and the
  owning gate (ADR 0025 or 0026) is re-passed, not patched over in `JobTrack.Web`.
- This acceptance depends on §3.1 of the phase-gate evidence plan: all nine previously
  `pending`/`partial` traceability rows (`TC-WEB-AUTHN-005`, `TC-WEB-AUTHN-007`, `TC-WEB-AUTHZ-001`
  through `004`, `TC-WEB-AUDIT-002`, `TC-DB-ROLES-002`, `TC-WEB-IDENT-003`) are closed with either a
  mapped existing test or a new TDD-built test, and one genuine gap (`TC-DB-ROLES-002`) was fixed at
  the database layer rather than accepted as residual risk.
- No §8.7 item was deferred to Phase 4 as residual risk; every bullet has direct, currently-passing
  evidence.
- Should a later phase expose a defect in an M8 decision (a missing authorization check, a
  problem-details leak, a browser regression), plan §1's rule applies: the correction is made in the
  web layer and this gate is re-passed.
