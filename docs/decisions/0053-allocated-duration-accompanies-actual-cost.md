# ADR 0053: Allocated duration accompanies actual labour cost

**Status:** Accepted
**Depends on:** ADR 0009, ADR 0017, ADR 0040, ADR 0042.

## Context

JobTrack calculated actual labour cost from concurrency-allocated session time, but its public cost
results discarded the aggregate duration after multiplying each exact segment share by its resolved
rate. Callers could inspect a bounded detailed trace and reconstruct some durations, but hierarchy,
bulk, and job-browsing projections exposed only money. A front end therefore could not display the
worked time underlying a node's cost without reproducing costing rules or fetching every session.

Raw summed session elapsed time is not the right answer. When one worker records concurrent sessions,
each session receives `1/N` of the overlapping segment. The duration shown beside cost must use that
same allocation or the displayed hours and money describe different quantities.

## Decision

- `AllocatedDuration` is an immutable domain value containing an exact rational tick quantity.
  Adding segment shares never converts them to decimal and therefore conserves thirds and other
  non-terminating fractions exactly.
- `AllocatedDuration.ToHours()` is the reporting boundary. It converts the exact aggregate once to
  decimal hours at six decimal places using midpoint-to-even rounding. No `double` or `float` enters
  the duration path.
- `AllocatedDuration.ToString()` is the human-display boundary. It rounds that decimal value to one
  decimal place using midpoint-to-even and always renders the digit, for example `3.0 hrs` or
  `3.5 hrs`. Machine-readable `allocatedHours` values retain `ToHours()` precision.
- Detailed, hierarchy, and bulk cost queries return allocated duration beside monetary cost. Job
  summary, subtree, and Awaiting Progress projections carry the same pair.
- Allocated duration has exactly the same authorization and availability as cost. If a node's cost is
  redacted or unavailable, its allocated duration is also absent. This prevents a new side channel
  around ADR 0040/0042 and keeps a cost field internally coherent.
- External cost and subtree responses expose decimal `allocatedHours` values. Staff UI cost fields
  render the pair as `£50.00 / 3.5 hrs` (one decimal place); the detailed cost report also presents
  total allocated time as its own metric.
- This does not expose individual `WorkSession` records on `/Requests/{id}`. ADR 0054 subsequently
  approves allocated-duration totals, without cost or session detail, as a narrowly scoped
  requester-visible projection.

## Consequences

- A branch's allocated duration is the exact sum of its descendant leaves, just as its actual cost is
  their monetary roll-up.
- A zero-cost node with no sessions reports zero allocated duration rather than omitting the duration
  key.
- Expected duration remains a planning estimate and is unaffected. “Allocated duration” always means
  concurrency-adjusted actual recorded work as of the cost query's captured instant.
