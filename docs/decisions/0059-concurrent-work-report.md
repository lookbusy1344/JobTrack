# ADR 0059: Concurrent work is reported as recorded overlap, outside the costing read scope

**Status:** Accepted
**Depends on:** ADR 0017, ADR 0041, ADR 0044.

## Context

Spec §4.4 permits one worker's sessions on *different* leaves to overlap deliberately, and §10.2
makes that overlap the concurrency divisor: an hour worked against two jobs at once contributes half
an hour, and half the money, to each. Until now nothing in the application answered the obvious
follow-up question — *which* other job absorbed the other half. A cost lower than the recorded hours
would suggest was visible, but its cause was not.

ADR 0017 deliberately withholds exactly that fact from **cost** results: a caller authorized for node
X never receives the identity, node, or rate of a foreign session that merely contributed to `N`,
and the residual inference ("the worker must have been busy elsewhere") is accepted rather than
mitigated. Read narrowly, that could be taken to forbid any feature naming the concurrent job.

It does not. ADR 0017 constrains the *costing* read scope — the elevated, unconditional whole-database
read the cost engine performs without consulting the caller's authorization. ADR 0041 separately
settled that recorded work is job data every baseline employee role may read, on its own merits: a
leaf's session list, including who worked it and when, is open to every employee, exactly as the job
tree, achievement, prerequisites, and readiness already are. A report built only from sessions the
caller could already read one leaf at a time discloses nothing new; it saves them from doing the join
by hand.

## Decision

**A "concurrent work" report exists as its own read, built from recorded sessions under the caller's
own baseline admission — never from the cost engine's elevated scope, and never carrying money.**

- `IJobQueries.GetConcurrentWorkAsync` reports, for one job, which other jobs its own workers were
  clocked on to at the same time: one row per (worker, other job), grouped by worker, carrying total
  overlap, the number of overlapping session pairs, and the window they span.
- **Overlap is raw wall-clock intersection of recorded sessions**, half-open (touching at a boundary
  is not overlap), with an unfinished session bounded by `asOf` exactly as costing bounds one. It is
  deliberately *not* the cost engine's allocated share: no schedule, working-time eligibility, or rate
  enters into it. The report says "these two jobs were being worked at once for this long", not "this
  is what it cost" — cost provenance stays on `/Jobs/CostReport` under its own `RateRead` gate.
- Admission is the baseline `JobDataAccessPolicy` gate every general job/work read shares (ADR 0041),
  and each concurrent job is described through the ordinary job-summary projection, so a caller sees
  a job they were already entitled to see listed and linked.
- ADR 0017 is **unchanged**: no cost result, breakdown line, or trace gains a foreign session's
  identity, node, or rate. The two coexist because they answer different questions from different
  inputs — one from an elevated costing read that must stay opaque, one from ordinary session data
  that is already open.

**Leaf-only for now.** Work sessions attach to `leaf_work`, so a branch has no sessions of its own
and the report would be vacuous for one. The page refuses a branch outright rather than rendering a
convincing empty table. A branch case (aggregating its subtree's leaves) is a future extension of the
same query, not a missing filter.

**Truncation is disclosed, never absorbed.** Both sides of the session load are bounded by
`ConcurrentWorkLimits`; hitting either cap sets `ConcurrentWorkResult.IsTruncated`, and the page says
the totals are a minimum rather than presenting a partial figure as complete.

## Consequences

- The overlap arithmetic is a pure domain calculator (`ConcurrentWorkCalculator`) over already-clipped
  intervals, unit-tested independently of any database; the persistence port does the same-worker,
  different-node overlap join in SQL on both providers, under one shared contract test.
- `/Jobs/ConcurrentWork` follows ADR 0044: it names its node as a link back to Browse, and Browse
  links to it from inside the Cost field — an "Info" aside on the figure it explains, since that
  figure is the question it answers. It therefore appears only where cost does, so a viewer without
  cost visibility (ADR 0042) is offered no breakdown of a total they were not shown.
- Because no figure on the page is money, the page needs no rate gate, and a worker can see why their
  own job's cost is lower than its elapsed hours without being granted cost visibility.
