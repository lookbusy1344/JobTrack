# External HTTP API Remediation Plan

**Date:** 2026-07-10  
**Status:** Implemented. Every §3 item is closed and mapped to passing tests in
`docs/traceability/test-catalogue.md`: pagination (§3.1) → `TC-DB-SESSION-001`, `TC-DB-PREREQ-004`,
`TC-DB-HIER-005`, `TC-APP-COST-007`, `TC-WEB-HIER-004`, `TC-WEB-SESSION-002`, `TC-WEB-PREREQ-003`,
`TC-WEB-COST-004`; OpenAPI contract (§3.2) → `TC-WEB-HIER-003`; bearer problem-details normalization
(§3.3) → `TC-WEB-TOKEN-008`, `TC-WEB-COST-003`; authorization boundary evidence (§3.4) →
`TC-WEB-SESSION-003`, `TC-WEB-PREREQ-004`, `TC-WEB-TOKEN-009/010/011`; route identity (§3.5) →
`TC-DB-SESSION-002`, `TC-WEB-SESSION-004`; idempotency documentation (§3.6) →
`TC-DB-SCHED-003`, `TC-WEB-PREREQ-005`, `TC-WEB-SCHED-001`, plus the idempotency table in
`docs/api/external-http-api-reference.md`; operational quality evidence (§3.7) → `TC-WEB-TOKEN-012/013`.
**Scope:** Remediate review findings in the implementation range starting at `528c9188`, against
`docs/plans/2026-07-09-external-http-api-plan.md` and ADRs 0029/0030.

## 1. Objective

Bring the external HTTP API implementation up to the acceptance bar in the external API plan before
claiming the milestone complete.

This plan is deliberately narrow. It does not expand the API to structural job commands, audit
browsing, or account administration. Those remain out of scope per ADR 0030.

## 2. Review Summary

The implementation has the right broad shape:

- PAT bearer authentication exists and is composed alongside cookie authentication.
- New API routes call `IJobTrackClient` rather than persistence services.
- Cancellation tokens are propagated into library calls.
- PAT storage is hashed, bounded by expiry, and revoked on the main library account transitions.
- A first-party sample client exists and is exercised against both SQLite and PostgreSQL hosts.

The implementation should not yet be treated as accepted because several plan acceptance checks are
not fully met.

## 3. Required Remediations

### 3.1 Add Pagination and Range Limits

**Problem:** The external API plan requires every growable collection to have pagination, filtering,
ordering, and maximum range limits. Current endpoints return unbounded arrays for growable
collections.

Affected routes include at least:

- `GET /api/jobs/{nodeId}/children`
- `GET /api/jobs/search`
- `GET /api/jobs/{nodeId}/sessions`
- `GET /api/jobs/{nodeId}/prerequisites`
- `GET /api/jobs/{nodeId}/cost`
- `GET /api/jobs/{nodeId}/cost/hierarchy`
- existing rate and schedule list responses where the collection can grow without a fixed product
  cap

**Work:**

- Define a shared external API paging contract, including named constants for default page size,
  maximum page size, and maximum time/range windows where applicable.
- Return a response envelope for paged collections containing `items`, `pageSize`, `continuation`
  or `offset`, and enough ordering metadata for deterministic client traversal.
- Add explicit ordering to every collection endpoint.
- Add bounded filters/ranges for cost trace and hierarchy responses rather than allowing an
  arbitrary subtree or trace to serialize unboundedly.
- Keep implementation through `IJobTrackClient`. If the library currently lacks bounded query
  shapes, add the library contract first and prove both providers.

**Tests first:**

- Contract tests asserting the OpenAPI schema exposes paging parameters and response metadata.
- Direct HTTP tests proving default limit, maximum limit, deterministic ordering, and rejection or
  clamping of excessive limits.
- Provider-equivalence tests for bounded results on SQLite and PostgreSQL.

### 3.2 Strengthen OpenAPI Contract Tests

**Problem:** Existing OpenAPI tests mostly assert that route strings are present. ADR 0030 requires
the contract test to include exactly the accepted route set and keep failing for routes outside
scope. The external API plan also requires authentication scheme, status families, problem-details
shapes, and security-sensitive redactions to be asserted.

**Work:**

- Parse `/openapi/v1.json` as JSON rather than using substring checks.
- Assert the supported route set exactly for the external release scope:
  job context, work sessions, prerequisites/achievement, cost reports, and the pre-existing
  rate/schedule surface.
- Assert structural job commands, audit browsing, and account administration are absent.
- Assert bearer authentication is represented in OpenAPI for protected API operations.
- Assert every operation documents representative `401`, `403`, `404`, `409`, `413`, and `429`
  problem responses where applicable.
- Assert schemas do not expose password hashes, security stamps, reset tokens, provider constraint
  names, or unrestricted sensitive data.

**Tests first:**

- A single route-set test that fails on missing supported routes or accidental out-of-scope routes.
- Per-category schema tests for security metadata and problem-details responses.

### 3.3 Normalize Bearer Authentication Failures to Problem Details

**Problem:** The reference document claims every non-2xx response is RFC 7807 problem-details JSON,
but the sample client comments acknowledge bearer challenge failures can be bare status codes with
empty bodies. That conflicts with the API documentation and the external API plan's
problem-details consistency requirement.

**Work:**

- Configure the PAT authentication handler or authentication events so malformed, missing, expired,
  and revoked bearer tokens return `application/problem+json`.
- Use a stable problem `type`, such as `/problems/authentication-required`, without disclosing
  whether a token was malformed, expired, revoked, or belonged to a disabled account.
- Update the sample client to expect a problem-details body where present, while remaining robust to
  empty bodies only if a lower middleware layer genuinely prevents problem generation.

**Tests first:**

- Direct HTTP tests for missing bearer token, empty bearer token, malformed token, expired token,
  revoked token, and disabled-account token.
- Each test asserts status `401`, content type `application/problem+json`, stable problem type, and
  no token state disclosure.

### 3.4 Complete Bearer Authorization Boundary Evidence

**Problem:** ADR 0029 requires direct HTTP tests for unauthorized, expired, revoked, disabled,
wrong-role, wrong-owner, sibling-subtree, and sensitive-data-denied cases. Current bearer tests cover
some of this but do not prove every role and ownership boundary exposed through the new routes.

**Work:**

- Enumerate each external endpoint's role and ownership boundary.
- Add bearer-authenticated tests for wrong-owner and sibling-subtree access on work sessions,
  prerequisites/achievement mutations, and cost reporting.
- Add sensitive-data denial tests proving an ordinary worker cannot infer rate/cost details or
  foreign-session identities through cost responses.
- Add tests for role-change revocation through the public library command path.
- Add tests for password reset, self-service password change, and emergency reset revocation where
  not already covered by existing suites.

**Tests first:**

- Table-driven HTTP authorization tests by endpoint category.
- Provider-specific or cross-provider integration tests for revocation triggers that live below
  `JobTrack.Web`.

### 3.5 Fix Route Identity Semantics

**Problem:** Some nested routes accept parent route identifiers that are not enforced by the
handler. For example, `POST /api/jobs/{nodeId}/sessions/{sessionId}/finish` and
`POST /api/jobs/{nodeId}/sessions/{sessionId}/correct` pass only `sessionId` to the library. A
request can therefore use a mismatched `{nodeId}` without that mismatch affecting the command.

**Work:**

- Decide whether nested identifiers are part of the resource identity.
- Prefer enforcing them: either pass `nodeId` into the library command or load/validate that the
  session belongs to the route node before mutation, without duplicating authorization rules in
  `JobTrack.Web`.
- Return `404` for mismatched parent/child route identifiers unless an ADR explicitly chooses a
  different policy.

**Tests first:**

- Direct HTTP tests where a valid `sessionId` is submitted under the wrong `nodeId`.
- Equivalent tests for any other nested routes whose parent identifiers can drift.

### 3.6 Document Idempotency Per Mutation

**Problem:** ADR 0030 requires every mutating command to record whether retry-safety comes from an
existing invariant or an idempotency-key mechanism. Some tests assert conflicts on retry, but the
route-level policy is not consistently documented.

**Work:**

- Add an idempotency table to `docs/api/external-http-api-reference.md` or a dedicated API decision
  note.
- For each mutation, record:
  route, command, concurrency token requirement, retry result, backing invariant, and whether an
  idempotency key is unnecessary.
- Add `Idempotency-Key` only if a mutation cannot be made retry-safe through existing invariants and
  concurrency tokens.

**Tests first:**

- One retry test per mutation category, including prerequisite add/remove and achievement update.
- OpenAPI or documentation tests if the project adopts docs-as-contract checks for idempotency
  notes.

### 3.7 Finish Operational Quality Evidence

**Problem:** The plan requires tests for timeout/cancellation, response content types, and
problem-details consistency. Current evidence covers rate limiting and bounded telemetry, but timeout
and cancellation evidence is not complete.

**Work:**

- Add a test seam that proves request cancellation reaches the library call for at least one read
  and one mutation.
- Add response content-type tests for success and representative problem responses across route
  categories.
- Add a response-size strategy for large result sets after pagination/range limits are implemented.
- Ensure telemetry includes useful bounded fields and never logs request bodies, token values, rate
  details, or cost details.

**Tests first:**

- Integration tests with a cancellable fake or test host composition where practical.
- Direct HTTP content-type matrix tests.
- Telemetry redaction tests using distinctive token/rate/cost values.

### 3.8 Re-run Gate Evidence

**Problem:** The milestone cannot be accepted without the external API verification gate and
API-specific evidence.

**Work:**

- Run the external API gate from the plan:

```bash
gtimeout 300 dotnet build JobTrack.slnx -warnaserror
gtimeout 120 dotnet format JobTrack.slnx --verify-no-changes
gtimeout 600 dotnet test JobTrack.slnx --no-build
gtimeout 300 dotnet list package --vulnerable --include-transitive
```

- Record any environmental failures separately from code failures.
- Update `docs/traceability/test-catalogue.md` with the new remediation tests.

## 4. Implementation Order

Use TDD for each item. The intended order is:

1. OpenAPI exact route-set and problem-details contract tests.
2. Bearer authentication problem-details normalization.
3. Pagination/range contract and library/provider support.
4. Authorization boundary and revocation evidence.
5. Nested route identity enforcement.
6. Mutation idempotency documentation and missing retry tests.
7. Operational timeout/cancellation and response-size evidence.
8. Gate run and traceability update.

This order front-loads externally observable contract failures before deeper refactoring. If
pagination requires new library query shapes, implement those in `JobTrack.Application` and both
persistence providers before changing the web layer.

## 5. Completion Criteria

This remediation is complete only when:

- every item in section 3 has a failing test committed before implementation;
- every growable external response is bounded and deterministic;
- every supported external route is present in the OpenAPI contract and every out-of-scope route is
  absent;
- every bearer authentication and authorization failure returns stable, non-disclosing problem
  details;
- nested route identifiers are enforced or explicitly documented by ADR;
- retry/idempotency behaviour is documented and tested for every mutation;
- SQLite and PostgreSQL prove equivalent public API behaviour for the client proof and bounded API
  workflows; and
- the external API verification gate passes under `gtimeout`.
