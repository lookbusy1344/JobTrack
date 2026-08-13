# Changelog

All notable changes to JobTrack are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project uses simple
`MAJOR.MINOR` release numbers.

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
