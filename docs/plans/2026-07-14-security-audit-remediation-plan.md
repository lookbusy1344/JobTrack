# Security Audit Remediation Plan

**Date:** 2026-07-14  
**Status:** Remediated
**Scope:** Fresh security audit of the current JobTrack implementation across database contracts,
reusable library, Identity adapter, external HTTP API, Razor Pages, operational host settings, and
tests. This does not re-open remediated findings from
`2026-07-11-security-review-remediation-plan.md` unless current code or later features create a new
gap.

## 1. Current Assessment

The security baseline is substantially stronger than the earlier review baseline:

- PostgreSQL role grants now have tests for readonly secret-column denial, PAT hash denial, and the
  emergency-reset boundary.
- PAT issuance, listing, revocation, and authentication reload current account state, deny disabled
  or locked actors, hash high-entropy tokens at rest, expose plaintext once, and have owner/admin
  management UI.
- HTTP API routes are authorization-gated, OpenAPI is no longer public, bearer failures use generic
  problem details, and API exception mapping no longer echoes raw domain messages.
- Browser and API paths have antiforgery where cookie-backed, strict cookie settings, CSP/security
  headers, no-store dynamic responses, bounded API telemetry, request-size limits, per-user API rate
  limiting, production fail-closed configuration guards, and real-Kestrel smoke evidence.
- The library and persistence layers generally enforce authorization inside transactions rather than
  trusting web-layer claims.

The remaining findings are not "missing basic controls"; they are cross-layer mismatches introduced
or exposed by newer account roles, credential flows, and audit expectations.

## 2. Findings

### 2.1 Requester Accounts Cannot Use Required Account Self-Service Surfaces

ADR 0033 makes `Requester` a normal local Identity role, not a separate identity boundary, and the
requester intake plan includes bearer-token requester API workflows. However:

- `/Account/ChangePassword` is authorized with `JobTrackPolicyNames.AnyEmployee`, whose role set
  intentionally excludes `Requester`.
- `/Account/ManageTwoFactor` is also `AnyEmployee`.
- `/Account/PersonalAccessTokens` is also `AnyEmployee`, leaving requester bearer-token workflows
  without a self-service issuance/revocation surface.
- `RequiresPasswordChangePageFilter` redirects every signed-in account with
  `RequiresPasswordChange` to `/Account/ChangePassword`, so a requester forced through a password
  change can be redirected to a page their own role cannot enter.

Impact: requester accounts can be locked out of mandatory credential rotation and cannot manage the
credential needed for the accepted requester bearer API. This is a policy mismatch, not just a UI
navigation issue.

Remediation:

- Add failing integration tests for a `Requester` with `RequiresPasswordChange = true` reaching and
  completing `/Account/ChangePassword`.
- Add tests for requester 2FA enrolment/disablement, or explicitly record an ADR if requesters must
  not use optional 2FA.
- Add tests for requester PAT issue/list/revoke if bearer-token requester workflows remain in
  scope; otherwise revise ADR 0033/requester API scope to remove requester bearer-token support.
- Introduce a named account-self-service policy, probably `AnyAuthenticatedUser`, for credential
  self-service pages whose operations are scoped to the signed-in actor and do not grant operational
  job/rate/audit access.
- Keep operational pages on `AnyEmployee`/`RequesterAccess` as currently designed.

### 2.2 Login and Two-Factor Attempt Rate Limiting Is Process-Wide

`LoginAttemptRateLimiter` is a single singleton fixed window shared by every caller. It is used by
both the password step and the TOTP step.

Impact: a remote caller can exhaust the global login budget and deny authentication to every user
for the window. This does slow credential stuffing, but it is a poor fit for the threat: it creates
a cheap site-wide availability attack and gives no separate control over per-origin, per-username,
or per-account abuse.

Remediation:

- Replace the singleton global counter with partitioned rate limiting.
- Partition at minimum by remote address plus normalized username for the password step, with a
  conservative global backstop only as defense in depth.
- Partition the two-factor step by challenge user plus remote address; do not let invalid TOTP
  attempts for one account consume every other user's login budget.
- Preserve existing Identity lockout semantics for repeated account-specific failures.
- Add tests proving one caller or one username cannot consume another user's login budget, while a
  high-volume attack against one partition is still throttled.
- Update `docs/threat-model/web-authentication-threat-model.md` row 1 with the partitioning model.

### 2.3 Authentication Event Auditing Is Incomplete

The primary spec requires auditing of authentication, logout, failed login, lockout, password
change, reset, account disablement, and permission changes without recording secrets. Domain and
credential-administration commands write `audit_event` rows, but the current browser sign-in
surface appears to rely on logs and Identity state for:

- successful login;
- failed password login;
- failed two-factor login;
- lockout caused by repeated failures;
- logout;
- self-service password change; and
- self-service 2FA enable/disable.

ADR 0037 also explicitly says 2FA enabled/disabled/admin-reset/CLI-reset events are audit kinds; the
admin reset path is audited, but self-service enable/disable is not.

Impact: security-sensitive account activity is not consistently present in the append-only audit
trail. Logs are useful telemetry, but they are not the same retention, authorization, and
append-only model as `audit_event`.

Remediation:

- Define a small authentication-audit writer that can be used by Razor Pages without exposing
  domain SQL in the web layer. Prefer a library/application command or port shape over direct web
  writes.
- Add audit event kinds for successful login, failed login, failed 2FA, lockout, logout,
  self-service password change, self-service 2FA enable, and self-service 2FA disable.
- Ensure failed-login audit rows do not become a username-enumeration channel in the audit UI. Use
  the actor id only when the account is known; otherwise record bounded, non-secret metadata or an
  intentionally redacted actor.
- Add tests that the audit payloads contain no password, TOTP code, password hash, security stamp,
  authenticator key, PAT plaintext, PAT hash, rate, or cost data.
- Add auditor/admin visibility tests for the new auth events.

### 2.4 Self-Service Two-Factor Changes Bypass Credential-Transition Consistency

Admin 2FA reset flows through `IEmployeeCommands.ResetTwoFactorAsync`, rotates the security stamp,
audits, and revokes PATs. Self-service `/Account/ManageTwoFactor` changes go directly through
`UserManager`:

- enabling TOTP is audited nowhere;
- disabling TOTP is audited nowhere;
- disabling TOTP does not revoke PATs;
- enabling/rotating the unconfirmed authenticator key can rotate the security stamp on page load,
  but only the current sign-in is refreshed.

Impact: self-service 2FA changes are a credential-sensitive transition with weaker consistency than
admin reset/password-change paths. Disabling 2FA in particular reduces account protection while
leaving bearer credentials active.

Remediation:

- Move self-service 2FA enable/disable through an application-layer command or a narrow identity
  service that applies the same audit and PAT-revocation rules as admin reset where appropriate.
- Decide whether TOTP enablement should revoke PATs. TOTP disablement should revoke PATs unless a
  new ADR explicitly accepts the weaker posture.
- Avoid rotating the security stamp merely by rendering the management page if the user has not
  submitted a change, or document and test the intended session behavior if this remains the
  design.
- Add tests for audit rows, PAT revocation, current-session refresh, and other-session invalidation
  for 2FA enable/disable.

## 3. Implementation Order

Use TDD for each slice:

1. Add requester account-self-service integration tests, then introduce the account-self-service
   authorization policy and update the three account pages.
2. Add partitioned login/TOTP rate-limit tests, then replace `LoginAttemptRateLimiter` with a
   partitioned implementation and update the threat model.
3. Add authentication-audit tests for login/logout/failure/lockout/password-change/2FA events, then
   introduce the audit writer/command surface.
4. Add self-service 2FA credential-transition tests, then route 2FA enable/disable through the
   audited/revoking path.
5. Re-run focused web integration tests, architecture tests, and the dependency advisory check.

## 4. Completion Criteria

This plan is remediated only when:

- requesters can complete mandatory password changes and whichever account self-service capabilities
  the requester API scope requires;
- one login attacker cannot exhaust every other user's login or 2FA attempt budget;
- authentication and credential self-service events are represented in append-only audit history
  without secrets;
- self-service 2FA enable/disable has the same audit, session, and PAT-revocation posture as other
  credential-sensitive transitions; and
- the plan is cross-linked from `docs/traceability/test-catalogue.md` with concrete test evidence.

## 5. Verification Commands

At remediation close, run:

```bash
gtimeout 300 dotnet build JobTrack.slnx -warnaserror
gtimeout 120 dotnet format JobTrack.slnx --verify-no-changes
gtimeout 300 ./scripts/fast-test.sh --build
gtimeout 180 dotnet test tests/JobTrack.Web.IntegrationTests --filter "FullyQualifiedName~AccountFlowTests|FullyQualifiedName~ChangePasswordTests|FullyQualifiedName~ManageTwoFactorTests|FullyQualifiedName~PersonalAccessTokenManagementTests|FullyQualifiedName~RequestsPageTests|FullyQualifiedName~RequestsApiTests|FullyQualifiedName~SensitiveLoggingTests"
gtimeout 120 dotnet test tests/JobTrack.ArchitectureTests
gtimeout 300 dotnet list package --vulnerable --include-transitive
```
