# ADR 0054: Requesters see allocated duration but not cost

**Status:** Accepted  
**Date:** 2026-07-29  
**Depends on:** ADR 0034, ADR 0053.

## Context

The requester detail page already exposes a read-only status snapshot of every node beneath the
original request anchor. After staff decompose a request, a requester can therefore see the
technical subsections but cannot tell how much recorded work each subsection represents.

Individual work sessions remain operational records: they identify workers and precise working
patterns and are outside the requester-safe projection. Monetary cost, rates, and rate provenance
are also explicitly staff-only. An aggregate duration is useful progress information without
disclosing either category.

## Decision

- Every node in `JobRequestDetailResult.Subtree` carries its concurrency-allocated
  `AllocatedDuration` as of the detail query.
- A leaf reports its own allocated duration. A branch, including a decomposed request anchor,
  reports the exact sum of its descendant leaves.
- The calculation reuses ADR 0053's exact rational allocation rules, including working-time
  eligibility and `1/N` allocation during concurrent sessions. It does not sum raw elapsed session
  intervals and does not require a cost rate to exist.
- Request access is authorized first through `RequesterAccessPolicy.CanView`. Only then may the
  application obtain the internal duration projection. A requester can never use this path for an
  unrelated node.
- `/Requests/{id}` renders a separate **Time worked** column. `GET /api/requests/{jobNodeId}`
  exposes the same value as decimal `allocatedHours` on each subtree node. The page renders time to
  one decimal place; the API retains ADR 0053's machine-readable precision.
- No cost, rate, worker identity, session boundary, session count, schedule, or session-management
  affordance is added to the requester projection.
- The Progress table uses the same branch/leaf glyphs, indentation, and tree connectors as Browse,
  but its node names remain plain read-only text rather than links into the staff workflow.

## Consequences

- Requesters can compare progress across decomposed subsections without gaining access to technical
  work records.
- A node with no eligible recorded work reports `0.0 hrs`; duration is not nullable in this permitted
  request projection.
- ADR 0053's rule tying duration visibility to cost still governs cost-bearing projections. This
  requester-specific projection is the explicitly approved disclosure exception anticipated by
  ADR 0053.
