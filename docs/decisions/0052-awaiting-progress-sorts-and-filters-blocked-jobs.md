# ADR 0052: Awaiting Progress sorts blocked jobs last and can hide them entirely

**Status:** Accepted
**Refines:** ADR 0051 (blocked is a state, not an error) for the flat attention list; ADR 0044 §
"`/Jobs/AwaitingProgress` complements Browse as the flat, priority/deadline-ordered attention list".

## Context

`/Jobs/AwaitingProgress` ordered its leaves by descending priority then ascending deadline, nulls
last, with readiness carried only as a per-row **Blocked** pill. Nothing else about a blocked leaf
was different: an Urgent leaf whose prerequisite has not succeeded sat at the very top of the list,
above every leaf someone could actually start right now.

That is the wrong shape for an attention list. ADR 0051 established that a blocked leaf is a valid
state that must stay visible rather than disappear or raise an error — but visible is not the same
as *first*. Priority describes how much the job matters; readiness describes whether anyone can act
on it at all. A queue that sorts by the former alone puts work nobody can pick up at the top of the
page and pushes actionable work below the fold.

Separately, some views of the list want blocked work gone altogether — a worker choosing what to do
next has no use for a row whose only available action is "wait for something else".

## Decision

**Readiness is the list's first ordering key.** `AwaitingProgressCalculator` orders ready leaves
before blocked ones, and only then by descending priority, ascending deadline (nulls last), and id.
Ordering among blocked leaves is unchanged — they keep the same priority/deadline sequence among
themselves, so the list reads as an actionable queue followed by a blocked tail. A blocked leaf of
any priority sorts below a ready leaf of any priority; this is deliberate, not a tie-break.

**`GetAwaitingProgressRequest.ExcludeBlocked` drops blocked leaves entirely.** It defaults to false,
so the existing "listed, marked blocked" behaviour is what a caller gets unless it asks otherwise.
`/Jobs/AwaitingProgress` exposes it as the "Hide blocked jobs" checkbox.

**Both are the query port's own work, not a post-filter.** Ordering and exclusion happen in
`IAwaitingProgressQueryPort`'s SQL, before offset/limit, so an excluded leaf never consumes a page
slot and readiness ordering is consistent across page boundaries — the same reason ownership,
subtree, and search scoping already live there (2026-07-25 scalability-follow-up plan §2.1).
PostgreSQL gains the `job_node_blocked()` stored function, the set-based counterpart to the
single-node `job_node_ready(id)`: it resolves each distinct required job's achievement once and then
descends from every blocking declaration point, rather than calling `job_node_ready` per candidate
row. SQLite mirrors it as the equivalent parameterized recursive CTE, as it does for every other
stored function.

**Every filter the dashboard offers is remembered per session.** Owner was already; the unassigned
pool, subtree scope (with its "show the whole tree" escape hatch), search text, and the new
blocked-job exclusion now are too, through `FilterMemory`'s flag and text forms. A request that
names a filter sets it and remembers it; a request that names none — the header nav link, a PRG
redirect that lost one — restores the last choice instead of resetting the view. `Offset` is
deliberately not remembered: it is a position within a result, not a filter.

## Consequences

- A blocked leaf can no longer be found by priority alone — a user scanning for an Urgent job must
  look at the blocked tail if it is not near the top. The **Blocked** pill and the deliberate
  grouping make that legible, and "Hide blocked jobs" makes it moot for anyone who does not want the
  tail at all.
- `IJobQueries.GetAwaitingProgressAsync`'s ordering contract changed for every caller, not just the
  web page. It is documented on the interface and asserted in
  `AwaitingProgressQueryPortContractTestsBase` against both providers.
- Readiness is now derived twice per request: in SQL for ordering/exclusion, and in
  `ReadinessCalculator` for each returned entry's `IsReady`. Both implement spec §6 and the contract
  tests assert they agree; the calculator remains the single authority for the flag itself, so the
  port's result shape is unchanged.
