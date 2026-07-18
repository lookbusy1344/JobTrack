# Temporal representation review and hardening plan

**Date:** 2026-07-12
**Status:** Implemented 2026-07-12 (both §4 decisions resolved 2026-07-12; all four slices landed
the same day — see commits `125f834d`, `fb4f250c`, `8791bb4c`, `9bcf95b0`)
**Scope:** Cross-provider datetime/timezone representation (SQLite vs PostgreSQL), the
Noda Time value-converter layer, and the gaps between ADR 0007/0008/0016 and the shipped
schema. Layered on top of `jobtrack_impl_plan.md` §6/§7; touches the database and reusable
library phases, so any defect here is fixed in those layers per the mandatory-order rule.

---

## 1. Findings — what the current design gets right (do not change)

The temporal model is deliberate and, on the whole, correct. Recorded here so the hardening
work below does not accidentally regress it.

- **Instants are provider-divergent but round-trip lossless.** SQLite stores every `Instant`
  as a signed 64-bit **UTC tick count** in an `INTEGER` column (ADR 0007); PostgreSQL maps
  `Instant` natively to `timestamptz` via Npgsql's NodaTime plugin. Both decode back through
  the domain's `Instant`, and the conformance suite asserts an instant written on one provider
  reads equal on the other. Integer tick comparison keeps the SQLite overlap triggers cheap;
  `double`/`julianday` is correctly rejected.
- **Civil values are kept distinct from instants.** Schedule `effective_start`/`effective_end`
  are calendar dates with no time component → fixed-width ISO-8601 `TEXT` on SQLite (sorts and
  range-compares correctly) / native `date` on PostgreSQL. Weekly-interval `start_time`/
  `end_time` are `LocalTime` → tick-of-day `INTEGER` (0..863_999_999_999) on SQLite / native
  `time` on PostgreSQL.
- **The zone is preserved, not collapsed.** This is the key point most schemas get wrong.
  `timestamptz` stores a UTC instant and *discards* the originating zone. This schema keeps the
  IANA zone id as a **separate column** (`app_user.iana_time_zone`,
  `user_schedule_version.iana_time_zone`), so "which zone was this schedule authored in" — which
  matters for future-dated recurring intervals whose offset can change under DST or legislation —
  is recoverable. Work sessions correctly store only the instant (a session *is* an instant; its
  display zone is the viewing user's), so no zone column is needed there.
- **DST resolution is centralised.** One composed `ZoneLocalMapping` resolver
  (`ReturnEarlier` + `ForwardShiftResolver`, ADR 0008) is wired as a single named domain
  singleton, not reconstructed per call site.
- **Overlap enforcement is provider-appropriate.** GiST exclusion constraints
  (`tstzrange`/`btree_gist`) on PostgreSQL; immediate half-open-interval triggers on SQLite,
  with unbounded ends expressed as `IS NULL` rather than an `'infinity'` sentinel.

**Verdict on the original question:** PostgreSQL's temporal model is far better specified than
SQLite's (which has *no* native date/time type — only the `TEXT`/`REAL`/`INTEGER` convention
layered on by its date functions, with no zone awareness and no validity enforcement). This
project bridges the gap correctly by owning instant/zone semantics in the Noda Time domain layer
and treating each store as a dumb, well-defined encoding. The gaps below are in the *edges* of
that model, not its core.

---

## 2. Findings — the gaps worth closing

### Gap A — TZDB version is never persisted, contradicting ADR 0008 (highest priority)

ADR 0008 states: *"Every computed cost or schedule result records the TZDB version
(`DateTimeZoneProviders.Tzdb.VersionId` at the time of calculation) bundled with the `asOf` and
persisted state"*, and defines reproduction of a historical result as
`(persisted state, asOf, TZDB version)`.

**No column, table, or field anywhere persists `Tzdb.VersionId`.** A `grep` for
`VersionId`/`tzdb_version` across `src/` and `database/` finds nothing. Consequently a historical
schedule/cost result cannot be reproduced under the ADR's own definition: after a TZDB bump that
corrects a zone's historical rules, there is no record of which ruleset produced the stored
figures, so "disclosed, not silently absorbed" is impossible to honour.

This is a genuine ADR-vs-implementation divergence and must be resolved one of two ways
(decision required — see §4): **persist it**, or **amend ADR 0008** to record the version only in
release notes and downgrade the reproducibility guarantee.

### Gap B — zone-id rot makes previously-valid rows unreadable, as a misleading 400

`iana_time_zone` is unconstrained `TEXT`/`text` on both providers. Every read port resolves it
with the **throwing** indexer `DateTimeZoneProviders.Tzdb[version.IanaTimeZone]`
(`SqliteScheduleQueryPort.cs:56`, `PostgreSqlScheduleQueryPort.cs:59`, both cost query ports).
If a TZDB upgrade removes or renames a zone (they do — e.g. merges into `America/…`), a row that
was valid at write time now throws `DateTimeZoneNotFoundException` **on read**.

The web boundary catches that exception and maps it to `400 Bad Request "time zone not
recognized"` (`JobTrackApi.cs:1132`). That mapping is correct for a *write* with a bad zone, but
wrong for a *read*: the client's GET was valid; the stored data rotted. It should surface as a
server-state fault (500-class) with an operational signal, not tell the caller their valid
request was invalid. No test exercises a stored-but-now-unknown zone.

### Gap C — zone ids are not canonicalised on write

The write path validates via the throwing indexer (accept/reject) but does not normalise. TZDB
aliases (`Asia/Calcutta`→`Asia/Kolkata`, `US/Eastern`→`America/New_York`, `Etc/UTC` vs `UTC`)
persist verbatim, so two users in the same real zone can carry different strings, weakening any
grouping/equality on the column and feeding Gap B (aliases are exactly the ids most likely to be
retired). Low severity, cheap to fix alongside B.

### Gap D — LocalTime sub-second parity across providers is untested

Instants have an explicit cross-provider equivalence test. `LocalTime` is stored with divergent
encodings — tick-of-day `INTEGER` (100 ns resolution) on SQLite vs native `time` (microsecond
resolution) on PostgreSQL. A `LocalTime` carrying sub-microsecond ticks would round-trip on
SQLite but **truncate on PostgreSQL**. Schedule times are whole minutes today, so this is latent,
not live — but it is an unasserted precision divergence of exactly the kind ADR 0007 took pains
to close for instants.

**Resolution (§4 decision):** the product accepts **whole-second** schedule resolution. Whole
seconds are represented losslessly by *both* encodings (SQLite tick-of-day and PostgreSQL
microsecond `time`), so the divergence cannot arise once fractional seconds are rejected at the
domain boundary. No provider-specific truncation rule is needed; the fix is a construction-time
guard plus a two-provider conformance test at second granularity.

---

## 3. Proposed work (TDD, per-slice contract → PostgreSQL → SQLite → conformance)

Ordered by priority. Each item is independently shippable; A and B/C are the substantive ones.
Both §4 decisions are resolved, so no slice is blocked.

### Slice 1 — persist the TZDB version for reproducibility (Gap A)

Decision: **persist**.

**Implementation note:** costs and schedules are computed live per query, not written to a
result-bearing table — there is no schema target for a `tzdb_version` column. `TzdbVersion` was
added to `CostDetailsResult`/`HierarchyTotalsResult` instead, captured at calculation time and
disclosed through the HTTP API response, which is what ADR 0008/0016 actually require ("every
computed cost or schedule result records the TZDB version"). No schema change was made.

1. Failing shared-contract test: a computed schedule/cost result carries the TZDB `VersionId`
   used, and it round-trips from persistence.
2. Schema: add `tzdb_version TEXT NOT NULL` to the result-bearing table(s) that ADR 0008 names.
   Pre-release, edit the existing `database/{postgresql,sqlite}/schema-versions/NNNN_*.sql` in
   place (per the CLAUDE.md pre-release convention), not a new forward migration.
3. Domain: thread `Tzdb.VersionId` captured at calculation time through the result records into
   the command ports (one source, referenced — no literal version string).
4. Provider enforcement tests (PostgreSQL then SQLite), then the conformance assertion.

### Slice 2 — make zone-id rot a server-state fault, not a client 400 (Gap B)

1. Failing test in each schedule/cost query-port contract suite: a persisted row whose
   `iana_time_zone` is not in the current TZDB, when read, throws a *domain* fault
   (a `JobTrackException`-hierarchy type, e.g. `UnknownStoredTimeZoneException`) — **not** a raw
   `DateTimeZoneNotFoundException`.
2. Read ports switch from the throwing indexer to `Tzdb.GetZoneOrNull(id)` + explicit throw of
   the new domain exception carrying the offending id and the row identity, so the failure names
   the corrupt data rather than the caller. This keeps the "exceptions are the sole failure
   channel" house rule and puts the type in the shallow hierarchy.
3. Web boundary: map the new exception to `500` (or `503`) with an operational log line, and
   keep the existing `DateTimeZoneNotFoundException → 400` mapping strictly for the *write* path
   where it is correct.

### Slice 3 — canonicalise zone ids on write (Gap C)

1. Failing test: writing a schedule/user with a TZDB alias persists the canonical id.
2. Add a single `CanonicaliseZoneId` helper in the domain (maps via
   `TzdbDateTimeZoneSource.Default.CanonicalIdMap`), applied at the write boundary where the zone
   is currently validated. One helper, referenced by both providers' command ports and the API.

### Slice 4 — LocalTime whole-second guard + cross-provider parity (Gap D)

Decision: **whole-second resolution**.

1. Failing domain test: constructing a schedule `LocalTime`/`WeeklyInterval` with a fractional
   (sub-second) component throws (framework `ArgumentException`-class, per the house error rule).
   The guard lives once in the domain constructor path, referenced — not duplicated per provider.
2. Failing conformance test mirroring the instant equivalence test: a whole-second `LocalTime`
   round-trips equal on **both** providers (SQLite tick-of-day and PostgreSQL `time`). Because
   fractional seconds are now rejected at construction, this asserts the encodings agree across
   the full accepted domain — no provider-specific truncation rule required.
3. If any current fixture or seed carries a sub-second schedule time, correct it to whole seconds
   (there should be none — schedule times are whole minutes today).

---

## 4. Decisions (resolved 2026-07-12)

1. **Gap A — persist `tzdb_version`.** Add the column and capture `Tzdb.VersionId` at calculation
   time, honouring ADR 0008 as written. A costing system is exactly where "which ruleset produced
   this number" is auditable-relevant, so the reproducibility guarantee is kept, not downgraded.
   ADR 0008 needs no amendment; a short ADR-status note referencing this plan is sufficient.
2. **Gap D — whole-second schedule resolution.** Reject fractional-second schedule `LocalTime` at
   the domain boundary. Whole seconds are lossless on both encodings (SQLite 100 ns tick-of-day,
   PostgreSQL microsecond `time`), so the divergence is designed out rather than papered over,
   while still giving second granularity (not the coarser whole-minute fallback). If a future
   requirement needs sub-second schedule times, this decision — and the parity/truncation
   question it sidesteps — is revisited then.

---

## 5. Explicit non-goals

- No change to the instant encoding (ADR 0007 is sound).
- No change to the date/`LocalTime` storage *types* — only the parity guard and the minute
  constraint if chosen.
- No move to a single "smart" provider-agnostic temporal abstraction; the divergent-encoding /
  domain-owns-semantics split is the correct design and stays.
- No attempt to store per-instant zones on `work_session` — a session is an instant by design.

---

## 6. Test & gate checklist

- New tests land failing-first, smallest implementation, then refactor (mandatory TDD order).
- Per-slice: shared contract → PostgreSQL enforcement → SQLite enforcement → conformance/race.
- Commit gate per slice: `dotnet build … -warnaserror`, `dotnet format … --verify-no-changes`,
  `./scripts/fast-test.sh --build`, plus a `--filter` run against the specific
  `JobTrack.Persistence.PostgreSql.Tests` / `JobTrack.Database.ContractTests` /
  `JobTrack.Web.IntegrationTests` class each slice touches.
- Full-solution suite once at the end of the plan, not per slice.
- All `dotnet`/`psql` calls run with `dangerouslyDisableSandbox: true`.
