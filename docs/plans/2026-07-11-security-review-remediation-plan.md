# Security Review Remediation Plan

**Date:** 2026-07-11  
**Status:** Remediated — all seven findings (§2.1-§2.7) closed; see `docs/traceability/test-catalogue.md`
for the test IDs proving each one.  
**Scope:** Security review of completed phases 1-3 plus the added external HTTP API/PAT work. This
plan intentionally excludes trivial issues and style-only hardening.

## 1. Review Baseline

The implementation has a strong security shape in several important areas:

- database invariants and provider equivalence are tested rather than left to application checks;
- the web app uses Identity cookies with strict cookie flags, antiforgery for cookie-backed JSON
  writes, restrictive CSP/security headers, and fail-closed production guards for forwarded headers
  and data-protection keys;
- the external API composes PAT bearer authentication with the cookie scheme and has exact OpenAPI
  route-set tests, per-user API rate limits, bounded telemetry, and pagination/result-size limits;
- cost trace exposure filters out foreign sessions before returning elevated-scope concurrency
  calculations; and
- PostgreSQL role grants separate readonly/application/deployer/emergency-reset responsibilities.

The remaining substantial findings are below. Treat them as security blockers before claiming a
production-hardening or external-API security gate.

## 2. Findings

### 2.1 PAT Issuance Does Not Reload Actor Account State

`PostgreSqlPersonalAccessTokenPort.IssueAsync` and `SqlitePersonalAccessTokenPort.IssueAsync`
authorize issuance with only `PersonalAccessTokenAccessPolicy.CanIssue(actor, target)`. They do not
reload the actor's current `identity_user` row or call `ActorAccountState.EnsureMayAct`, unlike
list/revoke/revoke-all. The PostgreSQL XML comment says the port reloads current roles before
writing, but issuance does not.

Impact: `IJobTrackClient.Tokens.IssueAsync` is a public library boundary. A disabled, locked, or
otherwise non-acting account can be represented by a stale or forged `CommandContext` in an
in-process caller and still mint a new bearer credential for itself. Web authentication currently
reduces practical exposure because a disabled user cannot normally sign in, but the library contract
is supposed to enforce account-state authorization itself.

Remediation:

- Add failing provider contract tests proving disabled and locked users cannot issue PATs on both
  PostgreSQL and SQLite.
- Reuse the same account-state reload path as `AuthorizeOrThrowAsync` for issuance.
- Keep self-service issuance (`actor == target`) but require the actor to exist and be allowed to
  act at the captured `CreatedAt`/operation time.
- Add a regression test proving an administrator still cannot issue a token for another user unless
  a new ADR deliberately changes the trust model.

### 2.2 PAT Issuance/Management Has No Web Surface Despite Threat-Model Claims

The threat model states PAT issuance is a cookie-authenticated, antiforgery-protected action reached
after sign-in. The web host, however, has bearer authentication and PAT validation but no page or
API route that issues, lists, or revokes PATs. Current API tests seed tokens directly through
`IJobTrackClient`.

Impact: operators have no first-class way to create or revoke CLI tokens in the product surface,
and the documented mitigation for credential stuffing against token issuance is not actually
implemented. This also leaves token theft response dependent on admin/library access rather than a
normal owner/admin workflow.

Remediation:

- Add a Razor Pages account-security screen for PAT issue/list/revoke, protected by cookie auth and
  antiforgery. Keep PAT issuance out of the bearer API unless a separate ADR justifies remote token
  minting.
- Show plaintext tokens exactly once, never store them in TempData/cookies/logs, and never render
  existing token secrets after issuance.
- Add owner revoke, administrator revoke, disabled-account denial, CSRF denial, and no-secret-cache
  tests.
- Update `docs/threat-model/web-authentication-threat-model.md` only after the implemented surface
  matches the documented mitigation.

### 2.3 Public Error Details Still Expose Internal Messages

`JobTrackApi.ExecuteAsync` maps several exception types directly to RFC 7807 `detail` using
`ex.Message`. Several library messages include concrete actor IDs, target IDs, token IDs, and
resource existence details. Authentication and middleware-level 403s are generic, but
library-level authorization/not-found/conflict responses are not consistently redacted.

Impact: direct API callers can use error bodies as an IDOR/oracle surface. Even where status-code
policy intentionally distinguishes 403 from 404, the response body should not expose actor IDs,
target user IDs, constraint names, provider details, or object-existence facts beyond the explicit
API contract.

Remediation:

- Introduce a central API exception-to-problem mapper with public-safe `detail` strings by category.
- Log the full exception message only in bounded server telemetry if it is known not to contain
  secrets or sensitive rate/cost data; otherwise log stable category/correlation only.
- Add tests for wrong-owner, sibling-subtree, missing entity, duplicate/conflict, and validation
  failures asserting no actor IDs, token IDs, provider names, constraint names, rate values, cost
  values, password/reset/security-stamp material, or raw exception text are exposed.
- Keep detailed domain exception messages for in-process library callers if needed; the redaction
  belongs at the HTTP boundary.

### 2.4 PAT Hashing Documentation and Storage Contract Drift

ADR/threat-model/schema comments say only a salted hash of the PAT is stored. The implementation
stores a plain SHA-256 hash of the high-entropy token string. Because the token has 256 bits of
server-generated entropy, this is not the same risk as hashing a human password, but the documented
control and the implemented control differ.

Impact: reviewers and operators may overestimate resistance to offline token guessing or table
correlation after database disclosure. The practical risk is moderate because the secret is random
and long-lived only within a bounded expiry, but the security contract should be precise.

Remediation:

- Decide and record one of two designs:
  - keep deterministic SHA-256 and amend ADR/threat-model/schema comments to say "one-way hash of a
    high-entropy random bearer token", with rationale that per-token salts are not needed for
    randomly generated 256-bit secrets; or
  - add per-token random salt or an application-level pepper and migrate both providers.
- If a pepper is chosen, define its secret source, rotation story, and recovery blast radius before
  implementation.
- Add tests/documentation proving plaintext is shown once, never persisted, never logged, and never
  returned by list/revoke APIs.

### 2.5 OpenAPI Is Publicly Served in All Environments

`MapOpenApi("/openapi/v1.json")` is registered before the authenticated `/api` route group and has
no environment or authorization guard.

Impact: route and schema discovery is available to unauthenticated network callers in production.
This is not equivalent to data exposure, and the schema tests already check for obvious secret
fields, but it materially lowers reconnaissance cost for an attacker against an employee-only
single-organisation system.

Remediation:

- Decide whether the OpenAPI document is public operationally. If not, require authentication and a
  role such as Administrator/JobManager, or serve it only in Development/test.
- Preserve integration tests by configuring the test host explicitly for whichever mode is needed.
- If left public, document that as an accepted exposure in the threat model and add a test that
  asserts the document contains no sensitive schemas, examples, internal exception strings, or
  out-of-scope routes.

### 2.6 Production Security Controls Lack Real-Host Evidence

`docs/operations/web-host-security.md` explicitly notes that Kestrel body-size enforcement and
request-timeout expiry are not proven by `WebApplicationFactory`. Program wiring is present, but
the production gate currently lacks a real-host smoke test for these controls and for HTTPS/proxy
header behavior under the deployed reverse-proxy shape.

Impact: these are exactly the controls most likely to differ between in-process tests and the
production hosting boundary. A mistake in reverse-proxy trust or request-size/timeout handling can
be a resource-exhaustion or scheme-confusion issue even if unit/integration tests pass.

Remediation:

- Add a small real-Kestrel security smoke suite, run under `gtimeout`, that boots `JobTrack.Web`
  against SQLite with production-like configuration and probes:
  - oversized body rejection without relying on `Content-Length` only;
  - slow body/request timeout behavior;
  - forwarded-proto/forwarded-for handling with configured trusted and untrusted proxy addresses;
  - HSTS and HTTPS redirection behavior.
- Keep the suite outside ordinary fast unit tests if runtime is high, but make it part of the
  production-hardening gate.

### 2.7 Database Role Evidence Should Include PAT Secrets and Emergency Reset

PostgreSQL grants now include `personal_access_token` and column-limited readonly access to
`identity_user`, but the security review should not rely on grant-script inspection alone.

Impact: a misgrant can expose credential-equivalent PAT hashes or allow the emergency-reset role to
issue tokens rather than only revoke them. This is a high-impact failure class because it bypasses
the application layer entirely.

Remediation:

- Extend PostgreSQL role-grant tests to assert:
  - readonly cannot select `identity_user.password_hash`, `security_stamp`, reset-grant hash fields,
    or `personal_access_token.token_hash`;
  - readonly can still read only the non-secret columns explicitly intended for reporting;
  - application cannot perform DDL, delete audit/history, or bypass append-only audit triggers;
  - emergency-reset can update password/reset state and revoke PATs but cannot insert PATs, assign
    roles, or mutate unrelated domain state.
- Add these checks to the phase-gate evidence, not just the security plan.

## 3. Implementation Order

Use TDD for each item:

1. Add provider tests for PAT issuance account-state enforcement, then fix both providers.
2. Add HTTP redaction tests for representative API failures, then centralize problem mapping.
3. Decide and document the PAT hash storage contract; implement only if changing storage.
4. Add the PAT owner/admin management surface and tests.
5. Decide OpenAPI exposure and enforce or document the decision.
6. Add PostgreSQL role-grant tests for PAT/Identity/emergency-reset boundaries.
7. Add real-host production security smoke tests for request size, timeout, and forwarded headers.
8. Re-run the relevant gate with `gtimeout` and update traceability.

## 4. Completion Criteria

This review is remediated only when:

- PAT issuance reloads current account state and denies disabled/locked actors on both providers;
- PAT creation/list/revoke has a tested owner/admin product surface or the API trust model is
  revised to explain how tokens are provisioned and revoked;
- API problem details are public-safe and no longer echo raw exception messages;
- PAT hashing documentation matches implementation, or implementation matches the documented
  salted/peppered design;
- OpenAPI production exposure is either authenticated/environment-scoped or explicitly accepted in
  the threat model;
- PostgreSQL role tests cover PAT hashes, Identity secret columns, and emergency-reset limits; and
- real-host evidence covers production-only request/proxy controls.
