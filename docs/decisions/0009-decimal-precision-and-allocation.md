# ADR 0009: Decimal precision, presentation rounding, and allocation-precision policy

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 6 (precision/allocation half — the reconciliation half is ADR 0002 (`0002-penny-reconciliation.md`))

## Decision

No `double` (or `float`) appears anywhere on the duration or money path — not in storage, not in the domain engine, not in any public contract (`CostLine.AllocatedHours`-style fields are `decimal` or a rational value object, never `double`).

### Duration/allocation precision

Segment duration is stored as **integer ticks** (matching ADR 0007 (`0007-sqlite-instant-encoding.md`)'s tick unit and PostgreSQL's `timestamptz` sub-second precision). Each active session's equal share of a segment with concurrency divisor `N` is the **exact rational** `segmentTicks / N`, represented as a `(segmentTicks, N)` pair (or an equivalent exact-rational value object) — it is never rounded back to whole ticks before further computation, and it is never converted to a rounded `decimal` before summation. Rounding a `1/3`-hour share to a `decimal` and then tripling it does not reproduce the original segment duration whenever `N` has a factor of 3, 7, or any other value that is not a power of 2 or 5; carrying the exact rational avoids this class of error entirely. Allocation-conservation property tests (ADR 0016 (`0016-noda-time.md`) is unrelated; see plan §7.2) assert **exact** rational equality across a segment's sessions, not a tolerance-based comparison.

### Monetary computation

The monetary contribution of one session within one constant-rate segment is a **single rounded division**:

```
rate × segmentTicks ÷ (N × ticksPerHour)
```

computed once, directly to `decimal`, at the precision recorded below — never as `round(share) × rate` (which reintroduces the same conservation error as rounding the duration share). `ticksPerHour` is a named constant derived from the tick unit fixed in ADR 0007 (`0007-sqlite-instant-encoding.md`), not a restated literal at each call site (no-magic-numbers convention).

Currency values that are computed as intermediate cost-engine inputs (rates, per-segment contributions) are carried at `numeric(19,6)` precision (PostgreSQL) / an equivalent fixed-point `decimal` precision (SQLite, application-enforced since SQLite has no native fixed-precision numeric type) — six decimal places gives adequate headroom above pennies for chained rate/time multiplication before the final rounding boundary. Currency is rounded to **pennies**, using **midpoint-to-even** (banker's) rounding, **only** at the accepted reporting boundary (a value the caller will display or export), consistent with ADR 0002 (`0002-penny-reconciliation.md`)'s reconciliation algorithm, which operates on already-penny-rounded values.

### GBP presentation

The initial release's currency is GBP; presentation rounds to whole pennies (2 decimal places) using midpoint-to-even. Multi-currency support, if it is ever added, is a distinct future ADR — this decision does not attempt to generalise ahead of that requirement.

## Consequences

- `numeric(19,6)` is the named precision constant referenced from schema DDL, EF value converters, and domain rate/money value objects — restated nowhere else as a bare literal.
- Analyzer/architecture tests (or a targeted `dotnet build` diagnostic) should flag any `double`/`float` usage inside `JobTrack.Domain`'s duration/money types and `JobTrack.Abstractions` money/rate value objects.
- The independent overlap oracle (plan §7.2) is exempted from the exact-rational representation only where it is deliberately a slower, structurally different witness (e.g. per-instant sampling) — its role is cross-checking, not production computation, and its own tests confirm it agrees with the engine to full precision on golden data, not merely "close enough."
