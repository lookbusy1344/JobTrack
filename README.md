# JobTrack - hierarchical job-tracking and costing

![JobTrack banner](JobTrack_banner.jpg)

A hierarchical job-tracking system with dynamic, historically-accurate costing.

[Live Google Cloud Run demo](https://jobtrack-web-zeb6shxnca-ew.a.run.app) with login `demo` and password `demo-jobtrack-1234`. Note the instance may take a couple of seconds to warm up when stopped, and recycles when not in use (the database is reset to a default state).

## Introduction

This is a recreation of my original project running on SQL Server and .NET 4.8, now rebuilt from scratch on PostgreSQL (or SQLite) and .NET 10, and packaged as a docker image for easy deployment.

The app has 2 unusual features:

- work is organised into **branches and leaves**, with a single root node; the tree can be arbitrarily deep, and any node can be moved to a new parent without losing its history or breaking its cost calculations. Actual work is done in leaves, which can be paused and resumed, and worked on by several people concurrently.
- **cost is computed live**, according to work schedules (with overrides), and concurrent work is split fairly across all participants. Job cost is never stored, and can be recomputed at any time for any node in the tree, even if the tree has been restructured since the work was done.

Additionally, all jobs can have prerequisites, and the system will automatically prevent work from being started on a node until all its prerequisites are complete. As with other features, this applies to both branches and leaves. Since branches don't have work themselves, a branch is considered complete when all its leaves are complete.

Two database backends are supported:

- **PostgreSQL** is the production backend, and is used in the live Google Cloud Run deployment. Supports multi-instance concurrent writes from multiple web hosts.
- **SQLite** is a fully conforming second provider, intended for embedded and demo use. Writes will be serialized.

## Overview

- **Stack:** .NET 10, C# 14, EF Core 10, Noda Time, ASP.NET Core Identity, Postgres/SQLite.
- **Shape:** a database and a reusable library, with three clients over them — see [Architecture](#architecture).
- **Two databases, one behaviour:** PostgreSQL is the production backend; SQLite is a fully
  conforming second provider for embedded and demo use, held equivalent by a shared contract-test suite.
- **Performance:** hundreds of thousands of jobs, and years of history have been planned for.
  Budgets and evidence: [`docs/traceability/performance-budgets.md`](docs/traceability/performance-budgets.md).
- **Security:** from first principles, defence-in-depth hardening, split least-privilege credentials, audited
  administrative actions, two-factor authentication, and a maintained threat model with
  every mitigation tied to a named test:
  [`docs/threat-model/web-authentication-threat-model.md`](docs/threat-model/web-authentication-threat-model.md).

## Architecture

More details: [`docs/architecture-overview.md`](docs/architecture-overview.md).

Broadly ports and adapters (hexagonal) — close to Clean Architecture, but not a doctrinaire
implementation of it: the database is treated as a layer that enforces its own invariants rather
than as a detail hidden behind a repository, and the library exposes one coarse facade instead of an
interface per use case.

Five layers, built and depended on strictly bottom-up. The database and library stack; the three
clients above them are siblings, each calling `IJobTrackClient` in-process — the web client does
**not** go through the HTTP API:

1. **Database** — PostgreSQL (production) or SQLite (eg embedded and demo use) as numbered
   forward-only SQL scripts applied by `JobTrack.Database`.
2. **Reusable library** — the domain, use cases and both EF Core persistence providers, behind the
   single `IJobTrackClient` facade; persistence is inverted, so each provider implements ports the
   application layer declares.
3. **HTTP API** — a token-authenticated versioned JSON API. This is for external clients; the
   web client does not need it. A future mobile app or SPA would connect here.
4. **Web client** — mobile-friendly as a first principle. Server-rendered Razor Pages rather than an SPA, to maximise compatibility: it
   works without client-side state on legacy browsers, and is intentionally conservative.
5. **Admin CLI** — uses the same library, to bootstrap the first administrator, create employees, emergency
   password and 2FA resets, and job-tree import.

The HTTP API and the web client share the one `JobTrack.Web` process; the admin CLI is its own
executable. Each process picks a database provider at startup and then reaches the database only
through `IJobTrackClient` — the dependency rules are asserted by the tests in
`tests/JobTrack.ArchitectureTests/`.

## Key documents

| If you want to… | Read |
| --- | --- |
| Build, test, run, or administer it locally | [`docs/developer-guide.md`](docs/developer-guide.md) |
| Understand how it behaves for its users | [`docs/behaviour-overview.md`](docs/behaviour-overview.md) |
| See the architecture and layers file by file | [`docs/architecture-overview.md`](docs/architecture-overview.md) |
| Deploy or operate it | [`docs/operations/postgresql-cloud-run-deployment.md`](docs/operations/postgresql-cloud-run-deployment.md) |

## Code size

Lines of code as counted by [`tokei`](https://github.com/XAMPPRocky/tokei) (blank lines and comments
excluded), as of 11 August 2026:

| Area | Files | Lines of code |
| --- | ---: | ---: |
| Product — `src/` | 721 | 36,888 |
| Tests — `tests/` | 426 | 71,291 |
| Database schema — `database/` | 41 | 2,210 |
| Sample API client — `samples/` | 19 | 1,079 |

The 70k lines of test code is a consequence of *Test Driven Development*, with over 3,000 tests in the full suite. It takes up to 10 minutes to run, even on a fast Mac.

A short test script, aiming to complete in about 20 seconds, is used for pre-commit checks [`scripts/fast-test.sh`](scripts/fast-test.sh)

## Status

**Release-ready.** All four delivery gates — database, reusable library, web application, and release — have formal, source-controlled acceptance records
([ADR 0025](docs/decisions/0025-m3-database-gate-acceptance.md),
[0026](docs/decisions/0026-m6-library-gate-acceptance.md),
[0027](docs/decisions/0027-m8-web-gate-acceptance.md),
[0063](docs/decisions/0063-release-gate-acceptance-and-risk-acceptance.md)). The codebase was built
test-first throughout (about two and a half lines of test for every line of product code), passes its full
solution and performance suites, and has been through three internal security audits, each fully
remediated. Performance is enforced: measured budgets on a 200,000-node
production-shape database run as regression ceilings on every performance-suite run.

The production deployment is PostgreSQL on Google Cloud (Cloud Run + Cloud SQL, with automated
backups and point-in-time recovery), defined by
[ADR 0062](docs/decisions/0062-cloud-run-cloud-sql-production-topology.md). 

[Live Google Cloud Run demo](https://jobtrack-web-zeb6shxnca-ew.a.run.app) (SQLite backend — a
demonstration configuration, not production).

The SQLite backend can be run in a throwaway docker container or as a persistent local database. See [`docs/operations/sqlite-limitations-and-configuration.md`](docs/operations/sqlite-limitations-and-configuration.md) for its limitations.

## Documentation map

**Design and specification**

- [`docs/jobtrack_spec_codex.md`](docs/jobtrack_spec_codex.md) — normative specification
  ([`docs/jobtrack_spec_claude.md`](docs/jobtrack_spec_claude.md) supplements it).
- [`docs/database-entities.md`](docs/database-entities.md) — core entities and the costing
  algorithm.
- [`docs/costing-engine.md`](docs/costing-engine.md) — the cost engine in depth: the
  boundary-partition algorithm and `1/N` concurrency allocation worked through a three-deep overlap,
  the PostgreSQL range column and GiST indexing behind it, and the EF Core materialization strategy.
- [`docs/api/jobtrack-client-design.md`](docs/api/jobtrack-client-design.md) — the `IJobTrackClient`
  facade; [`docs/api/external-http-api-reference.md`](docs/api/external-http-api-reference.md) —
  HTTP routes and auth model.
- [`docs/design-language.md`](docs/design-language.md) — the "Console" visual design system.
- [`docs/ownership-model.md`](docs/ownership-model.md) — node ownership, the unassigned pickup
  pool, and work authorization.
- [`docs/requester-user-guide.md`](docs/requester-user-guide.md) — submitting and tracking work as
  a `Requester`.

**Operations, security, and traceability**

- [`docs/operations/postgresql-cloud-run-deployment.md`](docs/operations/postgresql-cloud-run-deployment.md) —
  the production deployment (ADR 0062): provisioning, schema upgrades, rotation, emergency reset.
- [`docs/operations/production-deployment.md`](docs/operations/production-deployment.md) — the
  alternative self-hosted single-server runbook (ADR 0014).
- [`docs/operations/postgresql-backup-restore.md`](docs/operations/postgresql-backup-restore.md) —
  backup/restore procedure and the smoke test that proves it.
- [`docs/operations/docker-image.md`](docs/operations/docker-image.md) — the throwaway SQLite demo
  container.
- [`docs/operations/local-live-instance.md`](docs/operations/local-live-instance.md) — a single
  persistent local database for your own use.
- [`docs/operations/sqlite-limitations-and-configuration.md`](docs/operations/sqlite-limitations-and-configuration.md) —
  SQLite's operational envelope and required per-connection configuration.
- [`docs/operations/web-host-security.md`](docs/operations/web-host-security.md),
  [`docs/threat-model/web-authentication-threat-model.md`](docs/threat-model/web-authentication-threat-model.md) —
  host hardening and the authentication threat model.
- [`docs/operations/browser-testing.md`](docs/operations/browser-testing.md),
  [`docs/operations/hurl-smoke-tests.md`](docs/operations/hurl-smoke-tests.md) — the Playwright
  end-to-end suite and the `tests/hurl/` HTTP smoke suites.
- [`docs/operations/job-tree-import.md`](docs/operations/job-tree-import.md) — `AdminCli`
  `import-tree`: JSON format, validation, examples.
- [`docs/operations/global-tools.md`](docs/operations/global-tools.md),
  [`docs/operations/mutation-testing-gate.md`](docs/operations/mutation-testing-gate.md),
  [`docs/operations/package-metadata-gate.md`](docs/operations/package-metadata-gate.md) — required
  tooling and quality gates.
- [`docs/traceability/`](docs/traceability/) — test budgets, performance/scale budgets, and spike
  findings.

## License

AGPLv3. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
