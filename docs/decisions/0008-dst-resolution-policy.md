# ADR 0008: DST gap and repeated-time resolution policy

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 5

## Decision

Civil-time schedule expansion (weekly intervals and schedule exceptions, both defined as `LocalDateTime`/`LocalTime` per ADR 0016 (`0016-noda-time.md`)) resolves DST transitions using Noda Time's `ZoneLocalMapping` with the following explicit, non-default resolvers, applied uniformly wherever a civil-time schedule value is mapped to an `Instant`:

- **Gap (spring-forward, the local time never occurs).** Use `Resolvers.ForwardShiftResolver` semantics: shift the local time forward by the length of the gap. A schedule interval that starts inside a gap begins at the shifted instant; a schedule interval that ends inside a gap ends at the shifted instant. This means a gap can shorten a scheduled interval but never silently drops it.
- **Fold (autumn-back, the local time occurs twice).** Use the **earlier** of the two candidate instants (`Resolvers.LenientResolver`'s earlier-occurrence behaviour, not `AmbiguousTimeResolvers.ReturnEarlier` invoked ad hoc — the same rule is applied consistently through the shared resolver, not chosen per call site). A worker who works "through" a fold is scheduled/costed once at the earlier instant, not double-counted at both.
- The composed resolver used everywhere is `Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ForwardShiftResolver)`, wired as one shared, named resolver instance in the domain layer — not re-constructed per call site — so a future policy change is a one-line edit with full test coverage, not a scattered find-and-replace.

Every computed cost or schedule result records the **TZDB version** (`DateTimeZoneProviders.Tzdb.VersionId` at the time of calculation) bundled with the application, alongside the `asOf` and persisted state, per ADR 0016 (`0016-noda-time.md`). Reproduction of a historical result is defined as `(persisted state, asOf, TZDB version)`; a TZDB upgrade may deliberately change historical recalculation for zones whose historical rules were corrected, and this is disclosed (not silently absorbed) — the release notes for any TZDB bump call out affected zones, and recalculation is exercised as part of the DST spike (§5.3) and later regression tests.

## Consequences

- The composed resolver is a single internal `JobTrack.Domain` constant/singleton, unit-tested directly against known gap/fold transitions (spring-forward and autumn-back in a zone that observes DST, plus a zone that does not, plus a historical DST rule change) before schema and API contracts are frozen — this is exactly the §5.3 DST spike's job, and this ADR is the frozen output of that spike.
- Retaining old TZDB bundles is required only if the operational reproducibility policy (defined alongside ADR 0014 (`0014-single-server-deployment.md`)) demands recalculation under a specific older version; the default is "current TZDB, version disclosed," not "pin TZDB forever."
- A schedule interval that starts or ends inside a gap is a first-class boundary in the exhaustive cost-boundary set (§6.5) — the gap's forward-shifted instant is itself a boundary, not merely an input to boundary computation.

## Status note (2026-07-12)

`docs/plans/2026-07-12-temporal-representation-hardening-plan.md` closed the gap between this ADR and the shipped implementation: `CostDetailsResult`/`HierarchyTotalsResult` now carry `TzdbVersion`, captured at calculation time and disclosed through the HTTP API, satisfying this ADR's `(persisted state, asOf, TZDB version)` reproducibility triple. No amendment to the decision above was needed.
