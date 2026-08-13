# Rate resolution: the instantaneous hourly rate

Given one worker, one node, and one instant `t`, what hourly rate applies? This document works that
single question end to end — through the rota that decides whether `t` is even costable, the
effective-dated tables, the nearest-ancestor override walk, the four-level precedence, and the
provenance recorded with the answer.

The [cost engine](costing-engine.md) is the consumer: it partitions a worker's day into segments of
constant rate and concurrency, then asks this machinery for one rate per segment. Everything here is
"what does an hour cost for this person, here, now" — nothing about how those hours are shared or
summed. That is the cost engine's job.

Normative source: [`jobtrack_spec_codex.md`](jobtrack_spec_codex.md) §8 (schedules) and §9 (rates).
Entity shapes: [`database-entities.md`](database-entities.md). Decisions:
[ADR 0008](decisions/0008-dst-resolution-policy.md) (DST resolution),
[ADR 0016](decisions/0016-noda-time.md) (Noda Time / civil-time schedules).

---

## The two questions, in order

Resolving a rate at `t` is really two questions, answered in sequence:

1. **Is the worker on the clock at `t`?** — decided by the *rota*: the effective schedule version,
   its weekly civil-time intervals, and any schedule exceptions. If `t` falls outside the resulting
   effective working set, it is not costable and no rate is resolved — the instant is simply dropped.
2. **What rate applies at `t`?** — decided by the four-level *precedence* over priced exceptions,
   node overrides, user cost rates, and the user default.

The separation matters: `RateResolver` itself answers only question 2. It never checks eligibility —
the cost engine clips every session to the effective working set *first* (`clip_to_working_set`,
`ScheduleExceptionResolver`), so by the time a rate is requested, `t` is already known to be on the
clock. The one exception that bridges both questions is the **priced additive exception**, which both
*adds* eligible time and *prices* it (level 1 below).

---

## Question 1 — is the worker on the clock at `t`?

The effective working set for a worker over an interval is
`(scheduled intervals ∪ additive exceptions) − subtractive exceptions`
([`ScheduleExceptionResolver.Apply`](../src/JobTrack.Domain/Schedules/ScheduleExceptionResolver.cs)),
built in three steps.

### 1a. Which schedule version is effective

A worker's rota is versioned (`user_schedule_version`). Each version carries a civil `effective_start`
/`effective_end` **date** range and a snapshot of the worker's IANA zone
([`ScheduleVersion`](../src/JobTrack.Domain/Schedules/ScheduleVersion.cs)). Ranges are half-open and
**non-overlapping per user** (GiST `EXCLUDE`, schema 0009), so at most one version is effective on any
given calendar date. The zone is snapshotted *per version* — a version stays interpreted in the zone
recorded when it was current, even after the worker later moves zones.

Dates, not instants: a rota boundary is a calendar date with no time-of-day and no DST ambiguity of
its own (schema 0009 comment). `IsEffectiveOn(date)` is `date >= start && (end is null || date < end)`.

### 1b. Expand weekly intervals to instants

Each version holds recurring `WeeklyInterval`s — `(day-of-week, start-time, end-time)` in *civil*
wall-clock time, whole-second resolution, possibly `CrossesMidnight`
([`WeeklyInterval`](../src/JobTrack.Domain/Schedules/WeeklyInterval.cs)).
[`ScheduleExpander.Expand`](../src/JobTrack.Domain/Schedules/ScheduleExpander.cs) walks each calendar
date in range, and for each matching weekday maps the civil start/end to instants through
[`CivilTimeResolver`](../src/JobTrack.Domain/Schedules/CivilTimeResolver.cs):

- a spring-forward **gap** shifts the local time forward by the gap length;
- an autumn-back **fold** resolves to the *earlier* of the two candidate instants (ADR 0008).

Because a crossing interval or a DST shift can move an occurrence by up to a day relative to its
nominal date, the expander scans one day of slack either side, then normalizes and clips to the
requested bounds so no instant is double-counted.

### 1c. Apply exceptions

`user_schedule_exception` rows patch the expanded set: `AddWorkingTime` unions in extra availability,
`RemoveWorkingTime` cuts it out. **Subtractive wins on overlap** — a removal covering an addition
leaves no eligible time. The result is normalized into the final effective working set.

If `t` is not in that set, it is not costable. Done — no rate, no cost.

---

## Question 2 — the rate precedence at `t`

For every eligible instant,
[`RateResolver.Resolve`](../src/JobTrack.Domain/Rates/RateResolver.cs) returns the first hit down
this list (spec §9.3). Every candidate collection passed in is **already scoped to the one worker** —
the resolver knows only "which candidate rates," never "which worker."

| # | Source | `RateSource` | Rule |
|---|---|---|---|
| 1 | Priced additive schedule exception | `OvertimeException` | First priced `AddWorkingTime` exception whose interval `Contains(t)`. |
| 2 | Nearest node/ancestor override | `NodeOverride` | Walk the node's ancestor chain leaf→root; first ancestor with an override *for this worker* effective at `t` wins. |
| 3 | User cost rate | `UserCostRate` | The worker's effective-dated `user_cost_rate` covering `t`. |
| 4 | User default | `UserDefault` | `app_user.default_hourly_rate`. |

Falling through all four throws `MissingRateException` — a costable instant with no rate is a
defect, never a silent £0 (spec §9.3, ADR 0009).

### Level 1 — priced additive exception

Only an `AddWorkingTime` exception may carry a `RateOverride`
([`ScheduleExceptionEntry`](../src/JobTrack.Domain/Schedules/ScheduleExceptionEntry.cs); a
`RemoveWorkingTime` exception removes availability, so pricing it is meaningless and rejected at
construction). Priced additive exceptions **must not overlap per user** (partial GiST `EXCLUDE`,
schema 0010), so at most one applies at `t`. This is the only lever that both makes time eligible
(question 1) *and* prices it, which is why it outranks everything: overtime is worked outside the
ordinary rota at an explicitly agreed rate.

`RateResolver.FilterPricedExceptions` drops the overwhelmingly-common unpriced removals up front, so
resolving thousands of segments against one exception set scans only the priced few.

### Level 2 — the nearest-ancestor override walk

`node_rate_override` is keyed on **both** `node_id` and `user_id` (both `NOT NULL`, schema 0011):
there is no person-independent job rate. An override applies to its node *and every descendant*,
unless a closer descendant defines its own override for the same worker at `t`
([`NodeRateOverride`](../src/JobTrack.Domain/Rates/NodeRateOverride.cs)).

The resolver walks from the session's leaf up its `ParentId` chain, and at each node takes the first
override row for this worker that `IsEffectiveAt(t)`. **Nearest effective wins** — a nearer node with
no *currently effective* override does not shadow a further one; the walk continues past it. Overrides
for the same `(node, user)` pair are non-overlapping (GiST `EXCLUDE`, schema 0011), so a node
contributes at most one candidate at `t`.

Because the walk is per-worker, two workers on the same leaf can resolve to different ancestors, and a
distant override can start applying the instant a nearer one lapses. `RateResolver.IndexOverridesByNode`
groups overrides by node once per allocation set rather than per segment.

### Levels 3 & 4 — user cost rate, then default

With no exception and no override, the worker's own rate applies: their effective-dated `user_cost_rate`
covering `t` ([`UserCostRate`](../src/JobTrack.Domain/Rates/UserCostRate.cs); non-overlapping per user,
schema 0011), and failing that, `app_user.default_hourly_rate` — the floor that guarantees every
active worker has *some* rate.

---

## Effective-dating mechanics

Every dated rate table uses the same half-open convention:

- `IsEffectiveAt(t)` is `t >= EffectiveStart && (EffectiveEnd is null || t < EffectiveEnd)`.
- A `null` end means "still current."
- `EffectiveEnd` must be strictly after `EffectiveStart` (enforced in the value type's constructor and
  by a `CHECK` on both providers).
- Ranges are **non-overlapping** within their scope — per user for `user_cost_rate`, per `(node, user)`
  for `node_rate_override`, per user for priced additive exceptions and for schedule versions — so
  "the row effective at `t`" is always unique, never a tie to break.

Instant ranges (`timestamptz`) for rates and exceptions; civil **date** ranges (`date`) for schedule
versions — spec §9.1/§9.2 define rate effectiveness directly at an instant, with no zone step, whereas
a rota version turns over on a calendar date (schema 0009/0011 comments).

---

## Provenance

Every resolved rate carries its source
([`ResolvedRate(HourlyRate, RateSource)`](../src/JobTrack.Domain/Rates/ResolvedRate.cs)). The
[`RateSource`](../src/JobTrack.Domain/Rates/RateSource.cs) enum's numeric values *are* the precedence
order (`OvertimeException=1 … UserDefault=4`), and this is the "rate provenance" shown beside each
allocation in the cost report — a cost viewer sees not just the rate but which of the four levers
produced it.

---

## A single-instant example

Worker Dana, default £60/h, with a `node_rate_override` of £90/h on leaf B and £55/h on leaf C, and a
priced additive overtime exception of £100/h covering `[20:00, 22:00)` one evening.

| Instant `t` | On the clock? | Resolves to | Rate | Source |
|---|---|---|---|---|
| Leaf A, 10:00 (rota `[09:00,17:00)`) | yes (weekly interval) | no exception, no override on A or ancestors → user default | £60 | `UserDefault` |
| Leaf B, 10:00 | yes | override on B effective | £90 | `NodeOverride` |
| Leaf C, 10:00 | yes | override on C effective | £55 | `NodeOverride` |
| Leaf A, 21:00 | yes (additive exception adds it) | priced exception covers `t` | £100 | `OvertimeException` |
| Leaf A, 03:00 | **no** — outside rota, no additive exception | not costable | — | — |

The 03:00 row never reaches question 2: the working-set clip removes it before any rate is asked for.
The 21:00 row is eligible *only because* the priced exception added it, and that same exception prices
it — level 1 doing both jobs at once.

---

## Where it lives

| Concern | Location |
|---|---|
| Precedence resolution | [`src/JobTrack.Domain/Rates/RateResolver.cs`](../src/JobTrack.Domain/Rates/RateResolver.cs) |
| Rate value types | [`src/JobTrack.Domain/Rates/`](../src/JobTrack.Domain/Rates/) |
| Schedule expansion, exceptions, DST | [`src/JobTrack.Domain/Schedules/`](../src/JobTrack.Domain/Schedules/) |
| PostgreSQL query helpers | `resolve_rate`, `clip_to_working_set`, `user_rate_boundaries` (schema 0015; see spec_claude Appendix C) |
| Boundary set feeding segment partition | [`costing-engine.md`](costing-engine.md) §2.2, §2.5 |

The PostgreSQL functions mirror this exact precedence so the database can discover boundaries and
resolve rates in-query; the in-process `RateResolver` is the authority, and both are tested against
the same golden scenarios (spec §11).
