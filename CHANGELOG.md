# Changelog

All notable changes to JobTrack are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project uses
`MAJOR.MINOR.PATCH` release numbers.

## [1.3.1] — 2026-09-19

Fresh-eyes security review of the auth/authorization surface with v1.3.0's
passkey feature in focus, plus a follow-on remediation of the persistence
failure-classification gaps it surfaced. No high- or medium-severity issue
remained open at release.

### Security

- Anonymous passkey option and assertion requests now use distinct rate-limit
  key namespaces, both scoped to the remote address, closing a cross-origin
  limit-sharing gap.
- The in-process rate limiter's partition admission is atomic and evicts
  bounded, so a denied request can no longer churn unrelated partitions and an
  unseen caller is still admitted under capacity pressure.
- Password login now performs exactly one verification per attempt — a
  configured dummy hash is checked for unknown, disabled, or already-locked
  accounts — closing a timing side-channel that distinguished those cases from
  a live account with a wrong password. A lockout-tripping attempt is exempt,
  since it has already verified a real password.
- Password-change failures apply the shared account lockout ceiling and persist
  lockout state and audit atomically with the failure, across both providers.
- `/Account/ConfirmAccess`'s `returnUrl` is validated with the same
  `IsLocalUrl` guard the redirects already apply before being reflected into
  the form action and passkey URLs, closing a reflected-XSS and open-redirect
  exposure.

### Fixed

- A concurrency conflict when two eligible actors start the same fresh
  Waiting leaf at once no longer leaks a raw `DbUpdateConcurrencyException`
  out of the library; the loser gets a `ConcurrencyConflictException` and the
  Work page retries via PRG instead of a 500.
- `CorrectSessionAsync` rejects a correction that sets `StartedAt` in the
  future, matching the check `StartSessionAsync` already applies — previously
  it could lock a leaf out of every ending command.
- Provider write failures are classified as integrity, transient, or unknown
  instead of every catch-all treating a database failure as an invariant
  violation or a taken username. A deadlock, serialization failure, or busy
  database now surfaces as a 503 with `Retry-After: 1` (API) or a "database is
  busy, try again" page, instead of a false 409; a dropped connection, timeout,
  or resource exhaustion propagates unwrapped instead of being misreported.
  SQLite failures are classified by concrete result code rather than
  `DbException.IsTransient`, which SQLite does not implement.

### Changed

- Every `lock` statement uses .NET 10's `System.Threading.Lock` type, with an
  architecture guard covering C# and Razor to keep future lock targets on it.
- Every `InvariantViolationException` constraint id is a named constant
  (`ConstraintIds`) instead of an independently typed string literal at each
  throw and comparison site, fixing one drifted duplicate id along the way.
- The readiness index is built once per page instead of once per row,
  improving subtree and Awaiting Progress query performance.
- Updated NuGet dependencies (`Microsoft.NET.Test.Sdk`).

## [1.3.0] — 2026-09-15

### Added

- **Passkey sign-in** (ADR 0071). An employee can sign in with a passkey — Face
  ID, Touch ID, Windows Hello, or a security key — as an optional primary
  credential. Built on ASP.NET Core Identity 10's native WebAuthn support, no
  third-party library. The authentication model has three paths: username and
  password; username, password, and TOTP; or a passkey alone. A user-verified
  passkey needs no separate TOTP step.
- **Passkey management on `/Account/Security`** — a hub listing an employee's
  passkeys and TOTP state, with enrolment, rename, and remove ceremonies. The
  raw credential ID is never exposed.
- **Username-less passkey sign-in** on the login page, with progressive
  enhancement so the password form still works where WebAuthn is unavailable.
- **Passkey step-up** on `/Account/ConfirmAccess` for re-authentication.
- **Administrator passkey reset** on `/Admin/ManageEmployeeAccount` and an
  AdminCli `reset-passkeys` emergency command. Password fallback plus
  administrator reset is the whole recovery model — no passkey-only accounts, no
  public registration, no automated recovery.
- **Passkey-sign-in audit kinds**, and logging of enrolment failure reasons.

Every account keeps a password. Disabling the feature flag hides enrolment and
sign-in but retains stored credentials and leaves password and TOTP working.

### Changed

- Shortened the top-bar nav labels to **Security** and **API**.
- Gave `btn-outline-secondary` an accessible Console skin.
- Updated NuGet dependencies.

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
