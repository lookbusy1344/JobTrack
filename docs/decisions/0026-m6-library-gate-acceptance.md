# ADR 0026: M6 (reusable library) gate acceptance

**Status:** Accepted
**Closes:** Implementation plan §7.5

## Decision

M6 is formally accepted. Every §7.5 library-gate exit criterion is satisfied:

| Criterion | Evidence |
|---|---|
| Public API approval tests and compatibility baselines pass | `JobTrack.PublicApi.Tests` (11 tests) |
| Architecture tests prohibit forbidden project references and public provider types | `JobTrack.ArchitectureTests` (15 tests), including the rule that `JobTrack.Abstractions`/`Domain`/`Application` and both persistence providers carry no ASP.NET Core reference |
| Domain unit/property tests and both provider conformance suites pass | `JobTrack.Domain.Tests` (257 tests); `JobTrack.Persistence.PostgreSql.Tests` (17 test classes, 153 tests) and `JobTrack.Persistence.Sqlite.Tests` (18 test classes, 150 tests) run the same command/query-port behaviour against real PostgreSQL and SQLite |
| Mutation tests cover critical interval, authorization, prerequisite/readiness, and costing rules | `docs/operations/mutation-testing-gate.md`, refreshed 2026-07-09: 81.18% achieved against a 75% break threshold (170 mutants: 136 killed, 32 survived, 2 timeout); every survivor is triaged as a documented equivalent mutant or untestable content, none is a real coverage gap |
| Consumption is tested from a separate sample application for each provider | `samples/JobTrack.Sample.PostgreSql`, `samples/JobTrack.Sample.Sqlite` |
| Cancellation, timeout, disposal, retry boundaries, and telemetry are integration-tested | `JobTrack.PublicApi.Tests.ProviderIntegrationTests`: `PostgreSql_bootstrap_times_out_without_retrying_and_succeeds_when_the_caller_retries_after_releasing_the_lock` (timeout + retry), `PostgreSql_client_throws_object_disposed_after_the_shared_data_source_is_disposed` (disposal), `Sqlite_bootstrap_honours_a_pre_cancelled_token_without_writing_any_rows` (cancellation), `Sqlite_bootstrap_emits_a_bounded_activity_with_the_correlation_id_and_without_sensitive_tags` (telemetry) |
| NuGet package metadata, symbols, source link, deterministic output, and dependency vulnerability checks pass | `docs/operations/package-metadata-gate.md`; `dotnet pack -c Release` for all six packable projects (`JobTrack.Abstractions`, `JobTrack.Domain`, `JobTrack.Application`, `JobTrack.Persistence.Shared`, `JobTrack.Persistence.PostgreSql`, `JobTrack.Persistence.Sqlite`) produces matching `.nupkg`/`.snupkg` pairs; `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages across every project in the solution (2026-07-09 run) |
| No ASP.NET Core dependency exists in the reusable library layers | `JobTrack.ArchitectureTests`; ADR 0022 records the identity-adapter design that keeps `JobTrack.Identity` framework-free, with `JobTrack.Web`-only types (e.g. `JobTrackSignInManager`) carrying the ASP.NET Core Identity dependency instead |

## Consequences

- Per plan §7.5 and the mandatory implementation order in `CLAUDE.md`, Phase 3 ASP.NET Core front-end
  feature work may now proceed against the accepted library contract. The front end may not
  compensate for an incomplete library by adding domain SQL, persistence access, authorization
  rules, or costing behaviour locally — any such gap is fixed in the library layer and this gate is
  re-passed.
- The mutation-testing revalidation (plan §3.3 of this evidence-closure effort) surfaced one
  configuration fix, not a code regression: `dotnet-stryker` 4.16.0 requires `thresholds.low >=
  thresholds.break`, so `src/JobTrack.Domain/stryker-config.json`'s `low` was raised from 60 to 75.
  The pass/fail `break` threshold is unchanged at 75, and the achieved score (81.18%) still clears it
  with headroom. The two additional survivors versus the historical 2026-07-07 run are a correction
  to an undercounted category (`EmployeeAccessPolicy.cs` has three guarded methods, not one), not a
  new gap — see `docs/operations/mutation-testing-gate.md` for the full triage.
- Should a later phase (web) expose a defect in an M6 decision (a public API gap, a missed
  provider-conformance case, an untriaged mutation survivor), plan §1's rule applies: the correction
  is made in the library layer and this gate is re-passed, not patched over in `JobTrack.Web`.
