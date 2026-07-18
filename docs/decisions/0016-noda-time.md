# ADR 0016: Internal time library — Noda Time inside the domain

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 14

## Decision

Noda Time is the internal time library used throughout `JobTrack.Domain` and `JobTrack.Application`:

- **`Instant`** for every point-in-time value (session start/end, audit timestamps, schedule-version effective points) — never `DateTime`/`DateTimeOffset` internally.
- **IANA `DateTimeZone`** (via `DateTimeZoneProviders.Tzdb`) for every timezone reference — never Windows timezone identifiers or a hand-rolled offset table.
- **`LocalDateTime`/`LocalTime`** for civil-time recurring schedules (a weekly schedule interval is defined in local/wall-clock time, independent of any specific instant, until it is mapped against a specific date and zone).
- **Explicit `ZoneLocalMapping` resolvers** for mapping civil-time schedule values to instants across DST gaps and folds — the specific resolver composition is frozen by ADR 0008 (`0008-dst-resolution-policy.md`).

**`DateTimeOffset` is kept at the public boundary only** — HTTP API request/response DTOs, and any interop with libraries that require it (ASP.NET Core Identity's timestamps, for instance, at the `JobTrack.Identity` layer). Conversion happens at the edges (`JobTrack.Application`'s facade boundary and `JobTrack.Web`'s DTO mapping), never inside `JobTrack.Domain`.

**TZDB version disclosure.** Every result that depends on timezone/DST resolution records the TZDB version bundled with the application at calculation time (`DateTimeZoneProviders.Tzdb.VersionId`). Reproducing a historical result is defined by the triple `(persisted state, asOf, TZDB version)` — not by persisted state and `asOf` alone — because a TZDB correction to a zone's historical rules can legitimately change a recalculated historical result. This is disclosed, not treated as a bug, per ADR 0008 (`0008-dst-resolution-policy.md`).

## Why Noda Time over `DateTime`/`DateTimeOffset` alone

BCL `DateTime`/`DateTimeOffset` conflate "point in time" and "civil/local time" in ways that make DST-correct recurring-schedule modelling error-prone (`DateTime.Kind` is an easily-ignored convention, not a type-level guarantee; there is no first-class "local date + local time, no zone yet" type). Noda Time's `Instant`/`LocalDateTime`/`ZonedDateTime` split makes the distinction a compile-time type difference, and `ZoneLocalMapping` gives an explicit, testable seam for exactly the ambiguous/skipped-time policy this system must pin down (§5.3 DST spike, ADR 0008 (`0008-dst-resolution-policy.md`)).

## Consequences

- `Microsoft.Extensions.DependencyInjection`-registered `IClock` (Noda Time's testable clock abstraction) is the sole source of "now" for any operation that depends on current time, satisfying the "one captured clock value per operation" principle (plan §2) — a command captures one `Instant` at the start of its transaction and reuses it, never re-reading the clock mid-operation.
- EF value converters (§7.4) map `Instant <-> timestamptz` (PostgreSQL, via Npgsql's NodaTime plugin) and `Instant <-> long` ticks (SQLite, per ADR 0007 (`0007-sqlite-instant-encoding.md`)) — both providers store the same logical `Instant`, converted at the persistence boundary only.
- `JobTrack.Abstractions` and `JobTrack.Domain` take a package reference on `NodaTime` (and `NodaTime.Testing` in test projects for fake clocks); this is one of the two library dependencies flagged in the project layout as "not yet referenced, added when domain time modelling starts" (top-level `CLAUDE.md`) — this ADR is that addition's design record, added at the point domain time modelling actually begins.
