# JobTrack

![JobTrack banner](JobTrack_banner.jpg)

A hierarchical job/work-tracking system with dynamic, historically-accurate costing.

Jobs form a single-rooted tree of branches and leaves. A leaf holds real work sessions
(pause/resume without restructuring the tree), achievement rolls up recursively, and cost is never
a stored number: it is computed on demand from effective-dated labour rates, per-job rate
overrides, employee schedules, and the exact time worked — including splitting an employee's time
fairly across concurrent sessions.

Stack: .NET 10, C# 14, EF Core 10, Noda Time, ASP.NET Core Identity, xUnit + AwesomeAssertions.

[Live Google Cloud Run demo](https://jobtrack-web-zeb6shxnca-ew.a.run.app) (SQLite backend,
deployed by [`scripts/deploy-cloudrun.sh`](scripts/deploy-cloudrun.sh)). The real deployment story
is PostgreSQL — see [ADR 0014](docs/decisions/0014-single-server-deployment.md) and
[`docs/operations/production-deployment.md`](docs/operations/production-deployment.md).

## Start here

| If you want to… | Read |
| --- | --- |
| Build, test, run, or administer it locally | [`docs/developer-guide.md`](docs/developer-guide.md) |
| Understand how it behaves for its users | [`docs/behaviour-overview.md`](docs/behaviour-overview.md) |
| See the layers file by file | [`docs/architecture-overview.md`](docs/architecture-overview.md) |
| Contribute code | [`CLAUDE.md`](CLAUDE.md) — house style, TDD discipline, commit gate |

## Architecture

Built and layered strictly bottom-up, each layer calling only the one beneath it:

1. **Database** (`database/`, `JobTrack.Database`) — versioned schema scripts and invariants
   (constraints, triggers, stored functions) that hold regardless of what calls them.
2. **Reusable .NET library** (`JobTrack.Abstractions`/`Domain`/`Application` + the two persistence
   providers) — the cost engine, interval algebra, achievement rules, authorization, and audit,
   behind the single `IJobTrackClient` facade. Any .NET front end can consume it in-process;
   `JobTrack.AdminCli` and the samples do.
3. **External HTTP API** (`JobTrack.Web`, `/api/*`) — a JSON transport over `IJobTrackClient` for
   remote callers, authenticated by cookie session or personal access token.
4. **Web interface** (`JobTrack.Web`, Razor Pages) — the server-rendered browser front end. Layers
   3 and 4 share a host but neither ever bypasses `IJobTrackClient` to reach the database.

The shape is ports and adapters: dependencies point inwards to a pure, framework-free core that
knows nothing of EF Core, SQL, or ASP.NET Core, and the provider in play is a composition-root
choice no domain type can observe — asserted by `tests/JobTrack.ArchitectureTests`, not left to
good intentions. Two deliberate departures from orthodox Clean Architecture: the database is a real
layer enforcing its own invariants, and failure travels one channel only — exceptions, never a
`Result`-style return.

PostgreSQL is the authoritative production backend. SQLite is a fully conforming second provider —
every rule behaves identically on both, asserted by a shared contract-test suite — for embedded and
demo deployments where a database server isn't warranted.

The line count is dominated by tests: roughly 2:1 test-to-source, a property of the mandatory TDD
discipline.

## Scaling

Read paths are request-scoped, so latency tracks the size of the question, not the installation:
costing one leaf loads its own subtree and ancestor chains, never the table. Measured on PostgreSQL
against a 200,000-node production-shape tree: a single-leaf cost read runs in ~100 ms, the Awaiting
Progress worklist in under 120 ms at a realistic ~98% completion ratio, and a 400-leaf branch
costed against a 400-session worker in ~50 ms. Worker session discovery is GiST-indexed, prerequisite
fan-out resolves each required branch once regardless of dependents, and a serialized performance
lane (`scripts/perf-test.sh`) enforces every figure as a regression ceiling. Budgets, plans, and
`EXPLAIN` evidence live in
[`docs/traceability/performance-budgets.md`](docs/traceability/performance-budgets.md); an
installation of hundreds of thousands of jobs and years of session history is comfortably inside
the tested envelope.

## Documentation map

**Design and specification**

- [`docs/jobtrack_spec_codex.md`](docs/jobtrack_spec_codex.md) — normative specification
  ([`docs/jobtrack_spec_claude.md`](docs/jobtrack_spec_claude.md) supplements it).
- [`docs/plans/jobtrack_impl_plan.md`](docs/plans/jobtrack_impl_plan.md) — delivery plan and phase
  gates; [`docs/plans/README.md`](docs/plans/README.md) indexes every dated plan;
  `docs/decisions/*.md` are the ADRs.
- [`docs/database-entities.md`](docs/database-entities.md) — core entities and the costing
  algorithm.
- [`docs/api/jobtrack-client-design.md`](docs/api/jobtrack-client-design.md) — the `IJobTrackClient`
  facade; [`docs/api/external-http-api-reference.md`](docs/api/external-http-api-reference.md) —
  HTTP routes and auth model.
- [`docs/design-language.md`](docs/design-language.md) — the "Console" visual design system.
- [`docs/ownership-model.md`](docs/ownership-model.md) — node ownership, the unassigned pickup
  pool, and work authorization.
- [`docs/requester-user-guide.md`](docs/requester-user-guide.md) — submitting and tracking work as
  a `Requester`.

**Operations, security, and traceability**

- [`docs/operations/production-deployment.md`](docs/operations/production-deployment.md) — hosting
  runbook: service account, reverse proxy, Kestrel, PostgreSQL provisioning and access control.
- [`docs/operations/postgresql-backup-restore.md`](docs/operations/postgresql-backup-restore.md) —
  backup/restore procedure and the smoke test that proves it.
- [`docs/operations/docker-image.md`](docs/operations/docker-image.md) and
  [`docs/operations/postgresql-cloud-run-deployment.md`](docs/operations/postgresql-cloud-run-deployment.md) —
  the throwaway SQLite demo container and the persistent Cloud SQL configuration.
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

MIT. See [LICENSE](LICENSE).
