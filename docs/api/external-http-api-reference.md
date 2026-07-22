# External HTTP API reference

Human-readable companion to the generated OpenAPI document (`GET /openapi/v1.json` on a running
`JobTrack.Web` host) — this file explains auth, errors, and gives one example per resource; the
OpenAPI document is the machine-readable, always-current source of truth for exact schemas. For the
*design decisions* behind this surface (why these routes, why this auth mechanism, what's
deliberately excluded), see `docs/plans/2026-07-09-external-http-api-plan.md` and ADRs 0024, 0029,
0030 — this document does not restate that reasoning, only the resulting surface.

## Authentication

Every route below requires either:

- **Cookie** — the ASP.NET Core Identity session cookie a browser holds after signing in through
  `/Account/Login`. State-changing requests using the cookie additionally require the
  `X-CSRF-TOKEN` header, obtained from `GET /api/antiforgery-token`.
- **Bearer personal access token (PAT)** — `Authorization: Bearer <token>`, for non-browser
  clients (ADR 0029). A PAT authenticates strictly as its issuing user; every authorization,
  ownership, and data-sensitivity check downstream runs identically regardless of which scheme
  authenticated the caller. Bearer requests do **not** need the antiforgery header — a bearer token
  is never attached by a browser automatically, so it carries none of the ambient-credential risk
  antiforgery protects against.

There is no HTTP endpoint to issue a PAT — users and administrators manage tokens through the
signed-in Razor surface at `/Account/PersonalAccessTokens`, which calls `ITokenCommands` in-process.
Administrators may also issue tokens through `JobTrack.AdminCli` or another host composition root.
The one-time-revealed secret is handed to the client out of band. A PAT is
revoked automatically on account disablement, role changes, and password reset/change, alongside
that user's web sessions.

Every non-2xx response is an RFC 7807 problem-details JSON body (`Content-Type:
application/problem+json`) with a stable `type` URI you can branch on (`/problems/entity-not-found`,
`/problems/authorization-denied`, `/problems/validation`, `/problems/concurrency-conflict`,
`/problems/invariant-violation`, `/problems/prerequisite-blocked`,
`/problems/authentication-required`, `/problems/request-too-large`, `/problems/rate-limited`).

## Operational limits

- **Rate limiting** — each authenticated caller (by user identity, not by IP) gets its own fixed
  budget, separate from the browser login limiter. Exceeding it returns `429 Too Many Requests`
  with a `/problems/rate-limited` body.
- **Request body size** — capped (see `Program.cs`'s `MaxRequestBodyBytes`); an oversized body
  returns `413 Payload Too Large` with a `/problems/request-too-large` body.
- **Timeouts** — every request runs under a default server-side timeout; cancellation propagates
  into every underlying `IJobTrackClient` call.
- **Nested route identity** — a nested route's parent identifier is enforced, not decorative: a
  valid `sessionId` submitted under a `nodeId` that isn't actually its leaf (e.g.
  `/jobs/{nodeId}/sessions/{sessionId}/finish` or `.../correct`) returns `404`, identically to a
  nonexistent session.
- **Pagination** — every growable collection endpoint (`children`, `search`, `sessions`,
  `prerequisites`) accepts `offset` (default `0`) and `pageSize` (default `50`, max `200` —
  larger values are clamped, not rejected) query parameters and returns a paged envelope:
  `{ "items": [...], "offset": 0, "pageSize": 50, "hasMore": false, "orderedBy": "..." }`.
  `orderedBy` documents the deterministic sort so a client can page reliably. A negative `offset`
  or a non-positive explicit `pageSize` is rejected with `400`. Cost reports and one employee's
  rate/schedule snapshot are returned whole (not offset/limit-paginated, since reconciling a total
  needs the complete subtree/history) but are bounded by a hard maximum size, rejected with `400`
  if exceeded, rather than serialized unboundedly. Cost callers can request stricter limits with
  `maxTraceSegments` on `/jobs/{nodeId}/cost` and `maxHierarchyNodes` on
  `/jobs/{nodeId}/cost/hierarchy`; non-positive values or values above the service maximum are
  rejected with `400`.

## Idempotency

Per ADR 0030's policy, each mutation's retry-safety is decided against an existing database
invariant before considering a separate idempotency-key mechanism (ADR 0024's original test,
applied uniformly). No route on this surface has needed an `Idempotency-Key` header — every
mutation is already retry-safe through one of the mechanisms below.

| Route | Command | Concurrency token | Retry result | Backing invariant |
|---|---|---|---|---|
| `POST /jobs/{nodeId}/sessions` | Start session | None (create) | `409 Conflict` | `work-session-already-active`: a worker cannot have two open sessions on the same leaf. |
| `POST /jobs/{nodeId}/sessions/{sessionId}/finish` | Finish session | `version` (optimistic) | `409 Conflict` | A retry after success submits the now-stale `version`; concurrency check rejects it before any second finish can apply. |
| `POST /jobs/{nodeId}/sessions/{sessionId}/correct` | Correct session | `version` (optimistic) | `409 Conflict` | Same as finish: stale `version` on retry. |
| `POST /jobs/{nodeId}/prerequisites` | Add prerequisite | None (create) | `409 Conflict` | `job-prerequisite-already-exists`: the edge is a set member, not a counter: a retried add cannot double-apply. |
| `DELETE /jobs/{nodeId}/prerequisites/{requiredJobId}` | Remove prerequisite | None | `404 Not Found` | The edge no longer exists after the first successful delete; a retry finds nothing to remove rather than erroring or re-deleting. |
| `PUT /jobs/{nodeId}/achievement` | Set achievement | `version` (optimistic) | `409 Conflict` | Stale `version` on retry, same shape as session finish/correct. |
| `POST /jobs/{nodeId}/complete` | Complete leaf (ADR 0045) | leaf `version` and every `expectedActiveSessions[].version` | `409 Conflict` | Stale leaf `version`, or the leaf's actual active-session set no longer exactly matches `expectedActiveSessions` (a concurrent session start/finish moved it) — never silently included or excluded. |
| `POST /jobs/{nodeId}/reopen-and-start-session` | Reopen and start (ADR 0045) | `version` (optimistic) | `409 Conflict` | Stale `version` on retry, same shape as session finish/correct. |
| `POST /employees/{userId}/rates/user-cost-rates` | Add user cost rate | None (create) | `409 Conflict` | Effective-dated ranges may not overlap; a retried identical insert collides with the one just created. |
| `POST /employees/{userId}/rates/node-rate-overrides` | Add node rate override | None (create) | `409 Conflict` | Same overlap invariant as user cost rates. |
| `POST /employees/{userId}/schedule/versions` | Add schedule version | None (create) | `409 Conflict` | Effective-dated schedule versions may not overlap. |
| `POST /employees/{userId}/schedule/exceptions` | Add schedule exception | None (create) | `409 Conflict` | Exact duplicate exceptions are rejected as `schedule-exception-already-exists`; overlapping priced additive exceptions are rejected by the existing `user_schedule_exception_no_overlap_priced_additive` invariant. Non-identical unpriced/additive exceptions may still overlap by design. |

## Routes

All paths are relative to `/api`. `{nodeId}`, `{userId}`, `{sessionId}`, `{requiredJobId}` are
opaque `long` route identifiers.

### Job context (read-only; no ownership-based authorization gate — spec §7.3)

| Method | Path | Purpose |
|---|---|---|
| GET | `/jobs/root` | The permanent root node's detail. |
| GET | `/jobs/{nodeId}` | A node's full detail and root-first ancestor breadcrumb. |
| GET | `/jobs/{nodeId}/children` | A node's direct children, paged (`ownerUserId`, `archiveFilter` query filters). |
| GET | `/jobs/search` | Search node descriptions, paged (`searchText` required; `ownerUserId`, `archiveFilter` filters). |
| GET | `/jobs/{nodeId}/readiness` | Whether prerequisites are satisfied, and the blocker set if not. |
| GET | `/jobs/{nodeId}/subtree` | A bounded multi-level subtree rooted at a node (ADR 0039: `depth` optional, default 3, max 5; `ownerUserId`/`archiveFilter` filters). The cost roll-up (`rootTotal`, each node's `cost`) is included only when the actor may view it (ADR 0040: `Administrator`/`CostViewer`, or ownership of the queried root or an ancestor) — omitted as `null`, never a whole-request denial. |

### Work sessions (requires Administrator/JobManager/Worker)

| Method | Path | Purpose |
|---|---|---|
| GET | `/jobs/{nodeId}/sessions` | A worker's sessions on a leaf, paged (`workedByUserId` required query param). |
| POST | `/jobs/{nodeId}/sessions` | Start a session. Calling it again for an already-active worker/leaf pair is how a UI "resume" is expressed. |
| POST | `/jobs/{nodeId}/sessions/{sessionId}/finish` | Finish the active session ("pause"/"stop" in a UI). |
| POST | `/jobs/{nodeId}/sessions/{sessionId}/correct` | Correct a historical session's interval, with an audited reason. |

### Prerequisites and achievement

| Method | Path | Purpose |
|---|---|---|
| GET | `/jobs/{nodeId}/prerequisites` | Every prerequisite edge touching a node, in either direction, paged. |
| POST | `/jobs/{nodeId}/prerequisites` | Add a prerequisite edge (body: `requiredJobId`). |
| DELETE | `/jobs/{nodeId}/prerequisites/{requiredJobId}` | Remove a prerequisite edge. |
| GET | `/jobs/{nodeId}/achievement` | A leaf's current achievement state. |
| PUT | `/jobs/{nodeId}/achievement` | Transition achievement, with an audited reason. This primitive endpoint always requires Administrator/JobManager to reopen a terminal state (`Success`/`Cancelled`/`Unsuccessful`) back to `Waiting`, regardless of subtree ownership — it never starts a session and grants no wider authority. |
| POST | `/jobs/{nodeId}/complete` | Atomic composite (ADR 0045): finishes the exact caller-confirmed active-session set (`expectedActiveSessions`, possibly empty) at one instant and records `Success`, in one commit. Body: `version`, `expectedActiveSessions: [{ id, version }]`, optional `finishedAt`, optional `completionNote`. |
| POST | `/jobs/{nodeId}/reopen-and-start-session` | Atomic composite (ADR 0045): reopens a terminal leaf to `Waiting` with an audited `reason`, auto-advances to `InProgress` (ADR 0038), and starts `workedByUserId`'s session, in one commit. Authorized more widely than the primitive `PUT .../achievement` above — a controlling owner, Job Manager, or Administrator may start for any eligible target; a prior session participant on this leaf who controls nothing may start for themselves only (ADR 0045 §2). Body: `version`, `reason`, `workedByUserId`, optional `startedAt`. |

### Cost reports (requires Administrator/CostViewer)

| Method | Path | Purpose |
|---|---|---|
| GET | `/jobs/{nodeId}/cost` | Exact and displayed cost, with the rate-provenance segment trace (`asOf` optional, defaults to now; `maxTraceSegments` optional, max 50,000). |
| GET | `/jobs/{nodeId}/cost/hierarchy` | Reconciled cost totals for a node and its entire subtree (`asOf` optional, defaults to now; `maxHierarchyNodes` optional, max 50,000). |

### Employee rates and schedule (ADR 0024's original initial-release surface)

| Method | Path | Purpose |
|---|---|---|
| GET | `/employees/{userId}/rates` | One employee's user cost rates and node rate overrides. Bounded to 2,000 combined entries. |
| POST | `/employees/{userId}/rates/user-cost-rates` | Add an effective-dated user cost rate. |
| POST | `/employees/{userId}/rates/node-rate-overrides` | Add an effective-dated node rate override. |
| GET | `/employees/{userId}/schedule` | One employee's schedule versions and exceptions. Bounded to 2,000 combined entries. |
| POST | `/employees/{userId}/schedule/versions` | Add an effective-dated schedule version. |
| POST | `/employees/{userId}/schedule/exceptions` | Add a dated schedule exception. |

### Utility

| Method | Path | Purpose |
|---|---|---|
| GET | `/antiforgery-token` | A CSRF token for the `X-CSRF-TOKEN` header on cookie-authenticated writes. |

Deliberately **not** exposed (ADR 0030): structural job commands (create/edit/move/archive/
decompose), audit browsing, and account administration remain Razor-Pages/`JobTrack.AdminCli`-only
until a concrete non-browser need for them is identified.

On read responses, `kind` is a derived contextual label (Root/Branch/Leaf from parent and child
structure), not stored state. `hasChildren` and `hasLeafWork` expose structural facts for capability
decisions.

## Examples

### Read a node (bearer auth)

```http
GET /api/jobs/42 HTTP/1.1
Authorization: Bearer <personal-access-token>
```

```json
{
  "node": {
    "id": 42,
    "parentId": 1,
    "kind": "Leaf",
    "hasChildren": false,
    "hasLeafWork": true,
    "description": "Fit cabinets",
    "ownerUserId": 7,
    "priority": "Medium"
  },
  "ancestors": [
    { "id": 1, "description": "Root", "kind": "Root" }
  ]
}
```

### Start a work session (bearer auth)

```http
POST /api/jobs/42/sessions HTTP/1.1
Authorization: Bearer <personal-access-token>
Content-Type: application/json

{ "workedByUserId": 7 }
```

`201 Created`, `Location: /api/jobs/42/sessions/103`:

```json
{
  "id": 103,
  "leafWorkId": 42,
  "workedByUserId": 7,
  "startedAt": "2026-07-10T09:00:00+00:00",
  "finishedAt": null,
  "changedAt": "2026-07-10T09:00:00+00:00",
  "version": 1
}
```

Retrying this exact call while the session is still active returns `409 Conflict` with
`type: "/problems/invariant-violation"` — the library's own active-session invariant makes the
retry safe rather than duplicating the session (ADR 0030's idempotency review).

### Cost details (requires CostViewer or Administrator)

```http
GET /api/jobs/42/cost?asOf=2026-07-10T18:00:00%2B00:00 HTTP/1.1
Authorization: Bearer <personal-access-token>
```

```json
{
  "nodeId": 42,
  "exactCost": 137.500000,
  "displayedCost": 137.50,
  "trace": [
    {
      "segmentStart": "2026-07-10T09:00:00+00:00",
      "segmentEnd": "2026-07-10T14:30:00+00:00",
      "isWorkingTime": true,
      "activeSessionIds": [103],
      "sessionId": 103,
      "nodeId": 42,
      "segmentTicks": 198000000000,
      "concurrencyDivisor": 1,
      "amountPerHour": 25.00,
      "rateSource": "UserCostRate",
      "unroundedContribution": 137.500000
    }
  ]
}
```

A caller without `CostViewer`/`Administrator` gets `403 Forbidden` with
`type: "/problems/authorization-denied"` — cost visibility is never an unqualified baseline
capability (spec §7.3), unlike job browsing.

## Client proof

`samples/JobTrack.ExternalApiClient` is a first-party CLI client with **no project reference to
any `JobTrack.*` library assembly** — it talks only to the routes above, using its own plain-JSON
model types. `tests/JobTrack.Web.EndToEndTests/ExternalApiClientProofTests.cs` drives it against
both PostgreSQL and SQLite hosts, exercising authentication, a read workflow, a mutation workflow,
conflict handling, and revocation handling.
