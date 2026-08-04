# JobTrack

![JobTrack banner](JobTrack_banner.jpg)

A hierarchical job/work-tracking system with dynamic, historically-accurate costing.

JobTrack records hierarchical jobs, their prerequisites, actual work, achievement, employee
schedules, labour rates, and dynamically calculated costs. Jobs form a single-rooted tree of
branches and leaves; a leaf can have multiple work sessions (pause/resume without restructuring
the tree); achievement state is recursively derived from the hierarchy; and cost is calculated
dynamically from effective-dated labour rates, per-job rate overrides, and the actual time worked,
rather than stored as a static number.

Stack: .NET 10, C# 14, EF Core 10, Noda Time, ASP.NET Core Identity, xUnit + AwesomeAssertions.

[Live Google Cloud Run demo](https://jobtrack-web-716005672573.europe-west1.run.app)

That demo (SQLite backend) is deployed by [`scripts/deploy-cloudrun.sh`](scripts/deploy-cloudrun.sh),
which needs a running local Docker daemon — see
[`docs/operations/docker-image.md`](docs/operations/docker-image.md#cloud-run-smoke-test-2026-07-17).
See [ADR 0014](docs/decisions/0014-single-server-deployment.md) and
[`docs/operations/production-deployment.md`](docs/operations/production-deployment.md) for the real
deployment strategy using Postgres. The code here supports both backends.

## Start here

| If you want to… | Read |
| --- | --- |
| Build, test, run, or administer it locally | [`docs/developer-guide.md`](docs/developer-guide.md) |
| Understand how it behaves for its users | [`docs/behaviour-overview.md`](docs/behaviour-overview.md) |
| See the layers file by file | [`docs/architecture-overview.md`](docs/architecture-overview.md) |
| Contribute code | [`CLAUDE.md`](CLAUDE.md) — house style, TDD discipline, commit gate |

## Architecture

JobTrack is built and layered strictly bottom-up — database contracts, then the reusable library,
then the HTTP API, then the ASP.NET Core web interface — and each layer only ever calls the one
beneath it:

1. **Database** (`database/{postgresql,sqlite}/`, `JobTrack.Database`) — versioned schema scripts
   and the invariants (constraints, triggers) that hold regardless of what calls them. PostgreSQL
   is the primary backend with SQLite as a full-feature alternative.
2. **Reusable .NET library** (`JobTrack.Abstractions`/`Domain`/`Application` +
   `JobTrack.Persistence.{PostgreSql,Sqlite}`) — the cost engine, interval algebra, achievement
   rules, authorization, and audit, exposed through the single `IJobTrackClient` facade. Any .NET
   front end can consume this directly and in-process — `JobTrack.AdminCli` and the
   `samples/JobTrack.Sample.*` projects do exactly that, with no HTTP involved.
3. **External HTTP API** (`JobTrack.Web`, routes under `/api/*`) — a transport over
   `IJobTrackClient` for callers that are *not* on the same trusted host: a resource-oriented JSON
   API, authenticated by either the browser's cookie session or an opaque bearer personal access
   token, for non-browser/remote clients such as a CLI.
4. **ASP.NET Core web interface** (`JobTrack.Web`, Razor Pages) — the server-rendered browser
   front end, backed by ASP.NET Core Identity for authentication. Layers 3 and 4 are both hosted by
   `JobTrack.Web`, but neither ever bypasses `IJobTrackClient` to reach the database directly.

Supporting projects, not part of that chain:

- **Dual-provider persistence** — PostgreSQL (`JobTrack.Persistence.PostgreSql`) is the primary,
  authoritative datastore for production use. SQLite (`JobTrack.Persistence.Sqlite`) is not a
  reduced-feature fallback: it is a **fully conforming**, independently supported second backend —
  every domain rule, invariant, and query behaves identically on both, asserted by a shared
  contract-test suite — intended for embedded/single-node deployments where running a separate
  PostgreSQL server isn't warranted. The two are mutually exclusive per deployment (pick one via
  `Database:Provider`), not an automatic runtime failover from one to the other.
- **`JobTrack.AdminCli`** — a narrowly scoped CLI for one-time administrator bootstrap and
  emergency password reset, consuming the library in-process (layer 2), not over HTTP.

### Architectural philosophy

The shape is ports and adapters (hexagonal) — close to Clean Architecture, but not a doctrinaire
implementation of it. Dependencies point inwards only, towards a pure, framework-free core: the
domain and use-case layers know nothing about EF Core, SQL, or ASP.NET Core, and reach storage only
through port interfaces they define themselves and the two persistence providers implement. Which
provider is in play is a composition-root choice no domain type can observe, and the hosts sit
outside everything, calling `IJobTrackClient` and never a database. That separation is asserted by
`tests/JobTrack.ArchitectureTests`, not left to good intentions. It departs from orthodox Clean in
two deliberate ways: the database is a real layer enforcing its own invariants rather than a
swappable detail, and failure travels one channel only — exceptions, never a `Result`-style return.

The line count is dominated by the test suite: roughly a 2:1 test-to-source ratio, a property of the
mandatory TDD discipline rather than accumulated fat.

## Documentation map

**Design and specification**

- [`docs/jobtrack_spec_codex.md`](docs/jobtrack_spec_codex.md) — normative specification;
  [`docs/jobtrack_spec_claude.md`](docs/jobtrack_spec_claude.md) — supplementary detail.
- [`docs/plans/jobtrack_impl_plan.md`](docs/plans/jobtrack_impl_plan.md) — delivery plan: phase
  gates, milestone sequence, review prompts. `docs/decisions/*.md` are the ADRs closing
  product-semantic decisions, and [`docs/plans/README.md`](docs/plans/README.md) indexes every dated
  fix/remediation plan and its status.
- [`docs/database-entities.md`](docs/database-entities.md) — the core entities (job hierarchy, work
  sessions, prerequisites, rates) and the costing algorithm.
- [`docs/api/jobtrack-client-design.md`](docs/api/jobtrack-client-design.md) — the reusable
  library's `IJobTrackClient` facade: every command/query group, its request/result shapes, and the
  design rules applied throughout. [`docs/api/external-http-api-reference.md`](docs/api/external-http-api-reference.md)
  is the HTTP route table and auth model.
- [`docs/design-language.md`](docs/design-language.md) — the web front end's visual design system
  ("Console"): tokens, layout primitives, accessibility constraints.
- [`docs/ownership-model.md`](docs/ownership-model.md) — node ownership, the unassigned pickup pool,
  and owner-gated work-session authorization (ADR 0031/0032).
- [`docs/requester-user-guide.md`](docs/requester-user-guide.md) — end-user guide for submitting and
  tracking work with a `Requester` account;
  [`docs/plans/2026-07-11-client-requester-intake-plan.md`](docs/plans/2026-07-11-client-requester-intake-plan.md)
  documents the requester self-service intake behind it (ADR 0033/0034).

**Operations, security, and traceability** — each closing a specific gate item from the delivery plan

- [`docs/operations/production-deployment.md`](docs/operations/production-deployment.md) — hosting
  runbook for the single-server topology (ADR 0014): service account, reverse proxy, and Kestrel
  binding on Linux and Windows Server, plus PostgreSQL provisioning, tuning, and access control.
- [`docs/operations/postgresql-backup-restore.md`](docs/operations/postgresql-backup-restore.md) —
  what the automated backup/restore smoke test proves, and the manual procedure it models.
- [`docs/operations/docker-image.md`](docs/operations/docker-image.md) — the SQLite-backed container
  image for a throwaway local demo instance. Explicitly *not* the deployment story (ADR 0014 defers
  containers) and it ships known non-admin credentials, so it must never be network-exposed.
- [`docs/operations/local-live-instance.md`](docs/operations/local-live-instance.md) — running a
  single persistent local database for your own ongoing use, without the full production runbook.
- [`docs/operations/sqlite-limitations-and-configuration.md`](docs/operations/sqlite-limitations-and-configuration.md) —
  SQLite's operational envelope and required per-connection configuration.
- [`docs/operations/web-host-security.md`](docs/operations/web-host-security.md) — host-level
  configuration and filesystem permissions a `WebApplicationFactory` test can't exercise.
- [`docs/operations/browser-testing.md`](docs/operations/browser-testing.md) — Playwright setup and
  the real-browser end-to-end suite.
- [`docs/operations/hurl-smoke-tests.md`](docs/operations/hurl-smoke-tests.md) — `tests/hurl/*.hurl`
  suites exercising the HTTP API and web interface over a real HTTP connection.
- [`docs/operations/job-tree-import.md`](docs/operations/job-tree-import.md) — `AdminCli`'s
  `import-tree` command: JSON format, validation rules, worked examples.
- [`docs/operations/global-tools.md`](docs/operations/global-tools.md),
  [`docs/operations/mutation-testing-gate.md`](docs/operations/mutation-testing-gate.md), and
  [`docs/operations/package-metadata-gate.md`](docs/operations/package-metadata-gate.md) — required
  global CLI tools and what each quality gate checks.
- [`docs/threat-model/web-authentication-threat-model.md`](docs/threat-model/web-authentication-threat-model.md) —
  web application threat model and abuse-case test plan.
- [`docs/traceability/`](docs/traceability/) — test category/timeout budgets, performance/scale
  budgets, and the pre-implementation de-risking spike findings.

## License

MIT. See [LICENSE](LICENSE).
