# External HTTP API Plan

**Date:** 2026-07-09  
**Scope:** Define the work needed before JobTrack can claim a supported HTTP API for non-browser
clients such as a CLI, mobile app, or third-party integration.  
**Status:** Proposed.

## 1. Current Assessment

JobTrack already has an HTTP API, but it is not a general-purpose external client API.

The implemented surface is the narrow initial-release API accepted in ADR 0024:

- `GET /api/antiforgery-token`
- `GET /api/employees/{userId}/rates`
- `POST /api/employees/{userId}/rates/user-cost-rates`
- `POST /api/employees/{userId}/rates/node-rate-overrides`
- `GET /api/employees/{userId}/schedule`
- `POST /api/employees/{userId}/schedule/versions`
- `POST /api/employees/{userId}/schedule/exceptions`

That surface is useful for same-origin JSON interactions around employee rates and schedules. It is
not enough for a CLI or mobile application that needs the operational workflows currently available
only through Razor Pages: job tree browsing, structural job edits, work sessions, prerequisites,
achievement, cost reports, audit browsing, and account administration.

The current authentication model is also browser-shaped: ASP.NET Core Identity cookies plus
antiforgery tokens. That is correct for the server-rendered web app and the existing same-origin API
but awkward for non-browser clients. A supported CLI/mobile API needs a deliberate remote-client
authentication design rather than asking clients to emulate a browser login and CSRF flow.

## 2. Objective

Add a supported, documented, test-backed HTTP API for explicitly identified non-browser consumers
without weakening the existing Razor Pages workflows or duplicating domain rules in the web layer.

The API remains a transport over the accepted `JobTrack.Application` facade. It must not introduce
web-layer SQL, provider-specific behaviour, mutable persistence entities, or alternate
authorization/costing logic.

## 3. Decisions To Make First

Record these as ADRs before implementation:

1. **Client classes and trust model.** Decide which clients are supported first: first-party CLI,
   first-party mobile app, machine-to-machine integration, or third-party user-delegated clients.
2. **Authentication mechanism.** Choose the initial non-browser auth shape: bearer access tokens,
   opaque personal access tokens, OAuth 2.1/OIDC integration, mTLS-backed service credentials, or a
   narrower local-CLI mechanism. Do not reuse cookie-plus-antiforgery as the supported remote-client
   contract.
3. **Token lifetime and revocation.** Define expiry, refresh, rotation, revocation on disablement
   and password/security-sensitive changes, audit events, and emergency invalidation.
4. **API compatibility policy.** Decide whether the HTTP API is versioned from the first external
   release, and what constitutes a breaking change for routes, request DTOs, response DTOs, problem
   details, enum values, and pagination metadata.
5. **Exposure boundaries.** Decide which workflows are API-supported in the first external release
   and which remain browser-only. Do not infer "all Razor Pages" automatically.
6. **Idempotency policy.** Define command-level idempotency keys for retry-prone operations where
   database invariants alone do not prevent duplicate effects.

## 4. Work Order

### 4.1 Contract and threat model

**Work:**

- Add a threat-model update for non-browser clients: token theft, replay, confused-deputy access,
  credential stuffing against token issuance, excessive polling, IDOR, rate/cost data exfiltration,
  audit scraping, and mobile device loss.
- Define API consumer stories before adding endpoints. At minimum, decide whether the first client
  needs read-only job browsing, session capture, schedule/rate administration, cost reporting, or
  audit access.
- Write an OpenAPI contract test that fails for the intended external-client surface before
  implementing endpoints.
- Keep transport DTOs separate from the public library contracts and Razor Page models.

**Consumer stories (ADR 0029/0030).** The named consumer is a first-party CLI operated by an
authenticated human against a remote JobTrack.Web deployment. Concretely, that operator needs to:

1. Authenticate once with a PAT and reuse it across CLI invocations without re-entering a password
   each time (rules out cookie/antiforgery as the remote contract; ADR 0029).
2. Browse the job tree — list children of a node, search by name/kind, and check a node's readiness
   and ownership — before deciding what to work on or report on.
3. Start, finish, resume (a new session against the same leaf; concurrent sessions are valid per
   plan §8.5 slice 4), correct, and list their own work sessions from the command line instead of
   the browser, matching the day-to-day operational loop the plan identifies as the CLI's reason to
   exist.
4. Query what is blocking a node (unsatisfied prerequisites) and update achievement state as work
   completes, without opening a browser.
5. Pull an authorized cost report (their own subtree, or another subtree if their role permits) for
   scripting or offline analysis, with the same rate-provenance and foreign-session non-exposure
   guarantees the browser path already has (ADR 0017).

Explicitly not a consumer story for this release: restructuring the job tree (structural commands),
browsing the audit log, or administering accounts — ADR 0030 keeps those Razor-Pages/AdminCli-only
until a concrete non-browser need for them is identified.

**Acceptance checks:**

- Threat-model rows exist for each supported client class.
- OpenAPI tests assert the route set, status families, authentication scheme, problem-details
  shapes, and security-sensitive redactions.
- No endpoint is added without a named consumer story.

### 4.2 Authentication and authorization

**Work:**

- Implement the chosen non-browser authentication mechanism.
- Map authenticated API principals to `AppUserId` and library `CommandContext` without bypassing
  the library's role, account-state, ownership, subtree, and data-sensitivity checks.
- Ensure disablement, password reset, password change, role changes, and emergency reset revoke or
  invalidate remote-client access according to the ADR.
- Add direct HTTP tests for unauthorized, expired, revoked, disabled, wrong-role, wrong-owner,
  sibling-subtree, and sensitive-data-denied cases.

**Acceptance checks:**

- Non-browser clients can authenticate without scraping the login page or handling antiforgery
  tokens.
- Revocation evidence covers every security-sensitive account transition already required for web
  sessions.
- Authorization failures return RFC 7807 problem details and do not disclose entity existence beyond
  the chosen policy.

### 4.3 API surface expansion

Add endpoints in vertical slices, with tests first:

1. **Read-only job context:** browse/search job tree, node details, ownership, readiness, and archive
   filters.
2. **Work sessions:** start, finish, resume, correct, and list own/authorized sessions.
3. **Prerequisites and achievement:** edit prerequisites, query blockers, and update achievement.
4. **Structural job commands:** create, edit, move, archive, and decompose nodes with preview and
   optimistic-conflict recovery.
5. **Cost reports:** authorized cost summaries/details with rate provenance and no foreign-session
   leakage.
6. **Audit browsing:** bounded, paged audit queries with permission-sensitive projections.
7. **Account administration:** only if a non-browser administrative client is explicitly in scope;
   otherwise keep it Razor Pages/AdminCli-only.

**Acceptance checks:**

- Each endpoint calls `IJobTrackClient` rather than persistence/provider services.
- Every growable collection has pagination, filtering, ordering, and maximum range limits.
- Every mutation has explicit concurrency behaviour and either an idempotency strategy or a
  documented reason idempotency is unnecessary.
- PostgreSQL and SQLite integration tests prove equivalent public behaviour.

### 4.4 Operational API qualities

**Work:**

- Add request and response size limits appropriate to external clients.
- Add per-client and per-user rate limits distinct from browser login throttling.
- Propagate cancellation through every endpoint into the library call.
- Emit bounded telemetry with correlation IDs, client IDs where applicable, status family, duration,
  and stable failure category.
- Exclude credentials, tokens, reset data, unrestricted PII, rate details, and cost details from
  logs unless the actor is explicitly authorized and the value is part of the API response.

**Acceptance checks:**

- Tests cover timeout/cancellation, rate limiting, response content types, and problem-details
  consistency.
- Telemetry tests assert useful bounded fields and absence of secrets/rates/costs.
- OpenAPI examples cover success and representative problem responses.

### 4.5 Client proof

**Work:**

- Build a small first-party client proof against the API contract. Prefer the existing
  `JobTrack.AdminCli` only if the chosen use case is genuinely administrative; otherwise create a
  separate sample client under `samples/`.
- Exercise authentication, one read workflow, one mutation workflow, conflict handling, and
  revocation handling.
- Run the same client proof against PostgreSQL and SQLite-backed web hosts.

**Acceptance checks:**

- The client does not reference `JobTrack.Application` or persistence assemblies directly.
- The client uses the published OpenAPI/HTTP contract only.
- The proof is covered by integration or end-to-end tests and included in the release evidence for
  the external API milestone.

## 5. Non-Goals

- Do not replace the reusable .NET library for in-process first-party tools. A CLI running on the
  same trusted host may still use `JobTrack.Application` directly when that is the correct
  deployment model.
- Do not build a complete parallel HTTP surface until a real client needs it.
- Do not introduce a SPA or mobile app as part of this plan; this plan only makes the server API
  supportable.
- Do not move domain authorization, cost calculation, or database access into `JobTrack.Web`.
- Do not expose database identifiers, constraint names, provider errors, password hashes, security
  stamps, reset tokens, unrestricted rate data, or unauthorized cost detail.

## 6. Verification Gate

The external HTTP API milestone is accepted only when:

```bash
gtimeout 300 dotnet build JobTrack.slnx -warnaserror
gtimeout 120 dotnet format JobTrack.slnx --verify-no-changes
gtimeout 600 dotnet test JobTrack.slnx --no-build
gtimeout 300 dotnet list package --vulnerable --include-transitive
```

Additional API-specific evidence:

- OpenAPI contract tests cover every supported external endpoint.
- Direct HTTP authorization/security tests cover every role and ownership boundary exposed through
  the API.
- PostgreSQL and SQLite web integration or E2E tests exercise the same API workflows.
- A first-party client proof consumes only HTTP/OpenAPI and passes against both providers.
- The threat model and ADRs for client auth, compatibility, and endpoint scope are accepted.
