# Changelog

All notable changes to JobTrack are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project uses
`MAJOR.MINOR.PATCH` release numbers.

## [1.2.0] — 2026-08-24

### Changed

- **The Active column's "Unstarted" leaf status is merged into "Waiting"**
  (ADR 0070). An open leaf yet to be worked now reads *Waiting* whether or not
  a `leaf_work` record exists yet: starting one auto-attaches the record, so the
  two states were indistinguishable to the user and the split only leaked an
  internal detail. The unacknowledged-request status keeps its own distinct
  pill, and the terminal, paused, closed and active states are unchanged.
- Updated NuGet dependencies (Roslynator.Analyzers, AwesomeAssertions, FsCheck).

### Documentation

- Added an end-user **leaf status reference** — full name, short form, and
  meaning for every Active-column status — to `docs/behaviour-overview.md`,
  summarised as a quick table in the README.
- Documented the **computed branch rollup** (Success once every leaf beneath a
  branch has succeeded, Unfinished otherwise; derived at read time, never
  stored) in the same two places.

## [1.1.2] — 2026-08-19

### Changed

- Tightened the method-length architecture guard from 100 to 75 executable
  lines and added a hard file-length guard (1000 lines production/sample C#,
  500 Razor, 2000 test C#, no exception mechanism). Decomposed the eleven
  methods and nine files that exceeded the new ceilings — including
  `Program.Main` and `MapJobTrackApi` — without behaviour change. The file
  guard measures a C# file in code lines — lines carrying at least one token —
  so comments and blank lines no longer consume the budget; Razor stays on
  physical lines.
- Updated NuGet dependencies (Roslynator.Analyzers, xunit.runner.visualstudio).

### Fixed

- PostgreSQL command ports now truncate written timestamps to microsecond
  precision, so a re-read of an unchanged column can't disagree with the
  in-memory value returned from the write that produced it.
- Corrected culture handling and a missing connection string in the
  `jobtrack_live` launch profile that caused startup failures.
- Widened a browser-fixture readiness timeout and fixed fast-test failures
  surfaced by CI running in a dedicated repository.

## [1.1.1] — 2026-08-16

### Changed

- **Node rate overrides are forbidden on the root node** (ADR 0069). A root override
  applied to a worker's entire tree, silently outranking their own hourly rate — a
  restatement of `user_cost_rate`/`default_hourly_rate`, not a genuine per-node
  deviation. Attempting one now fails at the boundary.
- Hardened architecture guards: closed generic struct sizes are now measured, and
  the code style guards (including the nested-ternary and empty-property-pattern
  rules) are stricter.
- Updated NuGet dependencies.

## [1.1] — 2026-08-13

### Added

- **Browse status legibility.** Completed branches now show a *closed* status and
  are visually marked; inactive leaf states are distinguished rather than merged;
  the *Active* status is retained on narrow/phone displays instead of being dropped
  in the responsive reflow.
- **Complete jobs from Awaiting Progress**, shortening the common one-click
  completion workflow.
- **Concurrency-conflict logging** on every Razor Page recovery path and in the
  external HTTP API.
- **Failure logging** for refused deletes, API invariant violations, and
  missing-rate failures — previously silent paths now leave a trail.

### Changed

- Consolidated presentation primitives, request conventions, the integration test
  harness, scenario arrangements, actor/request persistence mechanics, and the
  application tracing lifecycle (code-reduction pass across ~483 files).
- New house-style rule forbidding nested ternary expressions, enforced by the
  renamed `CodeStyle_*` architecture guards; existing nested ternaries removed.
- Pinned SDK bumped to 10.0.400; `Dockerfile.postgresql` pinned to an image digest.
- Humanized README rewrite, refreshed code-volume figures, and updated v1.1
  external HTTP API reference.
- Monitoring notifies on incident open only.

### Fixed

- Deleting a job node now also removes its request-intake rows (ADR 0068).
- `work_session` deletion routed through a narrow function; cascade-delete moved
  out of an already-deployed schema version into a new forward-only script.
- Tightened privilege isolation for credential administration, retained-history
  deletion, and `job_request` DELETE grants; rate-limit function inputs constrained.
- Bounded readiness dependency probes; removed the split write-up autosave.
- Preserved outcomes in active pills, consolidated child row statuses, limited
  overdue child deadlines to open nodes, truncated long recently-visited node
  titles, and widened job descriptions on responsive layouts.

## [1.0] — 2026-08-11

Initial release.
