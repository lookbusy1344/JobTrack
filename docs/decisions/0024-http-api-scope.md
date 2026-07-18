# ADR 0024: Initial-release HTTP API scope

**Status:** Accepted
**Closes:** Fix plan §2.3 (2026-07-08), clarifying implementation plan §8.4

## Decision

§8.4 describes how the HTTP API should be designed (resource-oriented, OpenAPI-documented, RFC
7807 problem details, optimistic concurrency, pagination/idempotency where relevant) but does not
state *what surface area* it must cover for the initial release. The current implementation covers
only employee rate and schedule administration (`/api/employees/{userId}/rates*`,
`/api/employees/{userId}/schedule*`), while §8.5's ten browser-workflow slices (job tree browsing,
structural commands, leaf work/sessions, prerequisites, achievement, cost reports, audit browsing,
account provisioning) are Razor-Pages-only.

**The initial release's HTTP API is the narrow rate/schedule-administration surface already
implemented — not a complete API parallel to every §8.5 browser workflow.** Job tree browsing,
structural commands, leaf work/session management, prerequisites, achievement, cost reports, audit
browsing, and administrator account operations remain Razor-Pages-only for the initial release.

## Rationale

- The fix plan's own non-goals rule out introducing a SPA, Blazor, or bearer-token flow "without a
  concrete non-browser API consumer." No such consumer exists today. Building a complete parallel
  JSON API for all ten §8.5 slices without a consumer is speculative surface area: every added
  endpoint is a public-API compatibility commitment (`CLAUDE.md` "Public API discipline") and an
  additional attack surface (§8.2 threat model) that nothing currently calls.
- The rate/schedule API exists because it has a real, narrow reason to exist independent of the
  browser: those two resources are the ones most plausibly scripted or bulk-administered outside
  the interactive UI (payroll/rate imports, schedule bulk-loads). Job browsing, work sessions, and
  the rest of §8.5 have no analogous non-browser use case today.
- Building the full parallel surface "just in case" inverts the plan's own phase discipline (§1: a
  gate must pass before the next phase's feature work starts) — it would spend the current fix
  cycle on speculative API breadth instead of closing the concrete gaps (§2.1 CSRF, §2.2 UI
  foundation, §2.4 security plumbing, §2.5 browser evidence) that block the existing browser
  workflow from being release-ready.

## Consequences

- §2.3's remaining fix-plan work items (pagination/filter/range-limit tests for growable
  list/report endpoints, idempotency strategy for retry-prone commands) apply only to the
  rate/schedule endpoints that exist today, not to speculative future endpoints.
- If a genuine non-browser consumer (an external integration, a bulk-import tool, a future mobile
  client) is identified later, expanding the API to cover the relevant §8.5 workflow is a new,
  independently scoped and reviewed piece of work — not an automatic consequence of this decision.
  This ADR does not forbid that expansion; it only declines to build it speculatively now.
- The OpenAPI document and its tests continue to assert exactly the rate/schedule endpoint set;
  `The_openapi_document_lists_the_initial_rate_and_schedule_endpoints` (`HttpApiTests.cs`) remains
  the authoritative coverage assertion for what the API surface is expected to contain.

## Pagination and idempotency (fix plan §2.3 remaining acceptance checks)

With the API scoped to rate/schedule administration, both remaining §2.3 work items resolve
against the endpoints that actually exist, not speculative ones:

- **Pagination.** `GET .../rates` and `GET .../schedule` return one employee's full rate/schedule
  history in a single response. This is bounded by human data-entry cadence for a single employee
  (rate changes and schedule versions/exceptions over a career), not an unbounded or
  multi-employee collection — unlike, say, a global audit log. No pagination is added; if a future
  endpoint returns a genuinely unbounded or cross-employee collection, that endpoint adds
  pagination at the time it's introduced, not this one.
- **Idempotency.** `AddUserCostRate`, `AddNodeRateOverride`, and `AddScheduleVersion` already
  reject overlapping effective-dated ranges as a domain invariant (schema versions 0009/0010's
  triggers; surfaced as `409 Conflict` via `InvariantViolationException`). A retried identical
  request after a successful first attempt collides with the record the first attempt created and
  is rejected, not duplicated — the same mechanism that prevents two administrators from
  independently entering overlapping rates also makes accidental client retries safe. No separate
  idempotency-key mechanism is added for these three commands.
  `AddScheduleException` gets the same protection only for *priced additive* exceptions (schema
  version 0010's `user_schedule_exception_no_overlap_priced_additive_on_insert`/`_on_update`
  triggers) — plain-effect exceptions (e.g. `Unavailable`) have no overlap constraint, so a retried
  request can create a duplicate row. This is an accepted residual, not a fix-plan defect: whether
  duplicate non-priced exceptions over the same interval are legitimate (e.g. recorded separately
  for different reasons) or should be rejected is a domain-semantics question, not an HTTP-layer
  concern — adding web-layer deduplication would violate the fix plan's own non-goal against
  adding domain/persistence rules in the web layer. It is deferred to a domain-level ADR if and
  when it proves to matter in practice.
