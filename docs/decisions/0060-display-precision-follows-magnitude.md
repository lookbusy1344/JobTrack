# ADR 0060: Rendered cost and duration precision follows magnitude

**Status:** Accepted
**Depends on:** ADR 0002, ADR 0009, ADR 0053.

## Context

ADR 0053 fixed the human-display boundary for a cost pair at pennies and one decimal place, so it
renders as `£50.00 / 3.5 hrs` at every magnitude. That precision is right for the amounts most rows
carry, but it does not scale: a long-running branch reads `£1,055.76 / 152.4 hrs`, where the pennies
and the tenth are four characters of noise against four and three significant figures that already
say everything a reader scanning a column needs. The cost figure also competes for width with the
node's own description in the narrowest column of the busiest tables (Browse's subtree, Awaiting
Progress), and it is the column auto table layout squeezes first.

The minor digits are not merely redundant at that size — they are the reason the column is wide.

## Decision

- `Money.ToString()` renders pennies below £1,000 and whole pounds at or above it: `£234.50`,
  `£999.99`, `£1,056`. Rounding is .NET's `"N0"`, so `£1,055.76` renders `£1,056`, not `£1,055`.
- `AllocatedDuration.ToString()` renders one decimal place below 100 hours and whole hours at or
  above it: `3.5 hrs`, `99.9 hrs`, `152 hrs`. This narrows ADR 0053's "always renders the digit" to
  "always renders the digit below 100 hours".
- Both are rendering rules on the *default* format only. An explicit format argument always wins, so
  a caller that needs the pennies or the tenth at any magnitude asks for them and gets them.
- Neither rule touches a value. `Money.Amount` keeps its full `numeric(19,6)` precision,
  `AllocatedDuration.ToHours()` keeps its six decimal places, and machine-readable `cost`/
  `allocatedHours` fields on the external API are unaffected — they were never rendered through
  `ToString()`.
- `HourlyRate` is deliberately excluded. A rate is a rate-card input a reader checks digit for digit,
  not a scanned total, and the £1,000/hr case this rule would fire on does not arise.

## Consequences

- Rounding is visible. A £1,000.40 and a £1,000.60 cost both render as roughly `£1,000`; the exact
  amount is one page away in the detailed cost report, which formats explicitly.
- ADR 0002's largest-remainder reconciliation still guarantees that displayed child *values* sum to
  the displayed parent value. It does not guarantee that the rendered *strings* visibly do so once a
  parent crosses £1,000 while its children do not — two children rendering `£600.40` under a parent
  rendering `£1,201` is correct and expected.
- The threshold constants (`SterlingFormat.WholePoundsThreshold`,
  `AllocatedDuration.WholeHoursThreshold`) are declared once and referenced, per the project's
  no-magic-numbers rule.
