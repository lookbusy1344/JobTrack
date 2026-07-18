# ADR 0003: Historical schedule, exception, rate, and node-override correction semantics

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 15, §5.5 exit blocker

## Decision

Historical effective-dated rows — schedule versions, schedule exceptions, user rates, and node rate overrides — may be **corrected in place**: replaced or split by a Job manager, Rate manager (rates/overrides), or Administrator, even after sessions have already been costed against them. This is consistent with the existing session-correction rule (spec: workers/managers/administrators may correct historical sessions) rather than introducing a second, stricter regime for schedules and rates.

A correction is one of:

- **Replace**: change the value(s) of an existing effective-dated row without changing its effective range.
- **Split**: shorten an existing row's effective range and insert one or more new rows to cover the remainder, changing the value from a given instant/date onward without touching history before that point.
- **Retire**: end an effective range early with no replacement (only where the domain permits a gap, e.g. a schedule exception; not where continuous coverage is required).

Every correction requires:

- a mandatory reason string;
- an audit record capturing the full before and after row content (not just the changed fields);
- re-validation, inside the same transaction, of every invariant the corrected range participates in: non-overlap of schedule versions, non-overlap of explicitly priced additive exceptions, non-overlap of user-rate/node-override effective ranges, and (SQLite) the equivalent trigger-enforced checks — using the same deferred-constraint/exclusion mechanisms as ordinary inserts (§6.3, §6.4), not a bypass path;
- authorization at the correcting role (Rate manager for rates/overrides, Job manager or Administrator for schedules/exceptions) plus optimistic-concurrency version check, matching the general command shape (§7.3).

There is no locked cutoff after which history becomes immutable, and no "final" cost snapshot is taken at correction time: dynamically recalculated costs simply reflect corrected history the next time they are queried (spec: "historical schedule and rate changes dynamically affect calculated costs"). This is a deliberate consequence, not a defect — see the existing risk-and-control entry "Dynamic historical costing is mistaken for accounting finality."

## Consequences

- No additional "locked" or "final" state is added to schedules, exceptions, rates, or overrides — only the existing effective-dated row shape plus audit.
- The correction command reuses the ordinary overlap/non-overlap enforcement mechanisms; it must not disable or bypass constraints even transiently within the transaction.
- A race test is required: concurrent correction of one row plus a concurrent ordinary insert into the same overlap-checked set must not both commit.
- UI/API copy for any report that changed after a correction should make clear that costs are dynamically derived from current history, not restated from a fixed prior calculation.
