# Developer guide

Everything needed to build, test, run, and administer JobTrack locally. The
[README](../README.md) is the executive summary; [`CLAUDE.md`](../CLAUDE.md) is the house style and
commit gate; [`architecture-overview.md`](architecture-overview.md) is the file-by-file layer map.

## Requirements

- .NET SDK `10.0.301` or later within the same feature band (pinned in `global.json`).
- PostgreSQL, for the primary provider and its test suites — this project expects a local instance
  reachable at the Unix socket `/tmp:5432` (no Docker setup is provided). On macOS via Homebrew:

  ```bash
  brew install postgresql@18
  brew services start postgresql@18   # starts now and re-registers it to start at login
  pg_isready -h /tmp -p 5432          # confirm the server is accepting connections
  ```

  `brew services start` (rather than `pg_ctl`/`postgres` run directly) is what keeps the server
  running across reboots without a manual step every session. Stop it with
  `brew services stop postgresql@18`; see `brew services list` for current status.
- SQLite needs no separate service — it's a local file. It's the same fully supported provider, not
  a lesser one used just for dev; it's used for `JobTrack.Web`'s *local development*
  `appsettings.Development.json` override purely for convenience, since it needs no separate service
  to stand up. The application's own default (`appsettings.json`, i.e. what a real deployment gets
  absent that development override) is PostgreSQL.
- The [LibMan](https://learn.microsoft.com/aspnet/core/client-side/libman/) CLI, to restore the
  pinned client-side assets (Bootstrap and the Mulish display face) before running or browser-testing
  the web app — see "Client-side assets" below:

  ```bash
  dotnet tool install --global Microsoft.Web.LibraryManager.Cli
  ```

- [Playwright](https://playwright.dev/) browser binaries, only if you intend to run the real-browser
  end-to-end tests (not needed to build, unit-test, or run the app) — see
  [`operations/browser-testing.md`](operations/browser-testing.md).

## Build

```bash
dotnet build JobTrack.slnx
```

If a build fails with a flood of unrelated-looking errors — e.g. Razor `.cshtml` markup tags
(`input`, `label`, `form`) reported as missing C# types, or `<invalid-global-code>` — the cause is
usually a wedged MSBuild/Roslyn compiler-server process from a previous run, not a real code
problem. Shut it down and rebuild:

```bash
dotnet build-server shutdown
dotnet build JobTrack.slnx
```

### Client-side assets

`JobTrack.Web`'s client-side dependencies are pinned in `src/JobTrack.Web/libman.json` (Bootstrap and
the self-hosted Mulish display face) and restored into `wwwroot/lib/`, which is **git-ignored** — so a
fresh clone has neither until you restore them once:

```bash
cd src/JobTrack.Web && libman restore   # run from the directory holding libman.json
```

`dotnet build` does not do this for you. Skip it and the app still builds and runs, but every page
renders unstyled (no Bootstrap, no display face) and the end-to-end suite fails in a way that points
at the wrong thing — a pile of axe colour-contrast and layout failures rather than a missing
stylesheet. Re-run it after any change to `libman.json`, and bump versions there rather than editing
files under `wwwroot/lib/` by hand.

### NuGet lock files

Every project under `src/` commits a `packages.lock.json`. `Dockerfile.postgresql` restores against
them with `--locked-mode`, so the container build fails rather than silently resolving a different
dependency graph than the one reviewed — that is the point of committing them.

They are **regenerated, never hand-edited**. After changing a `PackageReference` or a version in
`Directory.Packages.props`:

```bash
dotnet restore JobTrack.slnx --force-evaluate
```

Commit the resulting lock-file changes alongside the package change. A plain `dotnet restore` will
not pick up a version bump on its own — it validates against the lock file instead, and fails with
`NU1004` if they disagree.

The container restores for a specific runtime (`-r linux-x64`, needed for ReadyToRun), and NuGet can
satisfy a locked-mode RID restore only from a RID-specific section of the lock file.
`src/Directory.Build.props` therefore declares `<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>`,
which makes an ordinary RID-less restore resolve that same graph. Without it the two restores fight:
a solution build rewrites the files without the RID section, and the next image build dies on
`NU1004: the project's runtime identifiers have changed`. If you ever see that error, check the RID
section survived rather than regenerating with an explicit `-r` — that fix does not stick.

That file is also why `src/` has its own `Directory.Build.props`: MSBuild stops at the first one it
finds walking up, so it imports the solution-wide file explicitly at the top. Settings that apply to
everything still belong in the root `Directory.Build.props`.

## Test

The per-commit gate (see [`CLAUDE.md`](../CLAUDE.md)): build, format, the fast core suite, plus a
targeted `--filter` run covering whatever the commit touches.

```bash
dotnet build JobTrack.slnx -warnaserror   # warnings are errors; analyzers + architecture tests enforced
dotnet format JobTrack.slnx
dotnet format JobTrack.slnx --verify-no-changes
./scripts/fast-test.sh --build
dotnet test tests/JobTrack.Persistence.PostgreSql.Tests --filter "FullyQualifiedName~TheClassYouChanged"
```

The full solution suite is for occasional use — once at the end of a multi-stage plan, or before
a commit that closes out a substantial piece of work — not for every commit, since it takes
several minutes:

```bash
dotnet test JobTrack.slnx
```

It runs every provider-conformance, domain, application, identity, architecture, and web test
project, against real (disposable, per-test-class) PostgreSQL and SQLite databases — a local
PostgreSQL instance must be reachable for the PostgreSQL-backed suites to pass. **It does not run
`JobTrack.Database.PerformanceTests`** — that project deliberately opts out of any solution-wide
`dotnet test` (see "Performance lane" below) so the full suite can always pass on its own,
regardless of shared-PostgreSQL-instance contention; run `./scripts/perf-test.sh` separately to
cover it. To run a single project instead of the whole solution:

```bash
dotnet test tests/JobTrack.Domain.Tests/JobTrack.Domain.Tests.csproj
```

### Fast core suite

`dotnet test JobTrack.slnx` takes several minutes, most of it in the PostgreSQL/SQLite
contract, performance, and browser suites. For a rapid pre-commit sanity check (under 20
seconds), run only the projects with no external database or browser dependency:

```bash
./scripts/fast-test.sh
```

This runs `JobTrack.Domain.Tests`, `JobTrack.Application.Tests`, `JobTrack.ArchitectureTests`,
`JobTrack.Identity.Tests`, `JobTrack.Persistence.Shared.Tests`, `JobTrack.Persistence.Sqlite.Tests`,
and `JobTrack.PublicApi.Tests` with `--no-build` (pass `--build` to build `JobTrack.FastCore.slnf` --
this suite's own dependency closure, not the whole solution -- exactly once first, e.g. on a clean
checkout; every project then runs `--no-build`, rather than each of the seven separately restoring
and building the graph they all share). It covers domain/application logic, architecture-fitness
rules, public API surface, and SQLite (file-based, no server needed), and skips PostgreSQL-backed,
web-integration, and browser end-to-end coverage entirely — which is why the commit gate pairs it
with a targeted `--filter` run against whichever of those projects the commit actually touches.

The 20-second budget is informational by default: a warning if exceeded, but the script still exits
0, since a commit gate must never fail just because the machine running it is briefly loaded. Pass
`--strict` for a CI-style mode that exits non-zero on a real overrun; the interactive commit gate in
`CLAUDE.md` uses the default informational mode.

For a broader check before a commit (under 80 seconds), add `--longer` (or `-l`):

```bash
./scripts/fast-test.sh --longer
```

This runs the fast core suite above plus `JobTrack.Database.ContractTests`,
`JobTrack.Persistence.PostgreSql.Tests`, and `JobTrack.Web.IntegrationTests` — the highest-value
PostgreSQL-backed, provider-specific concurrency, and web-host integration coverage — while still
skipping `JobTrack.Database.PerformanceTests`, `JobTrack.AdminCli.Tests`, and the real-browser
`JobTrack.Web.EndToEndTests` suite. `--longer` combines with `--build` (e.g. `--longer --build`).

### Performance lane

`JobTrack.Database.PerformanceTests`' latency ceilings
([`traceability/performance-budgets.md`](traceability/performance-budgets.md)) are measured and
enforced against one deterministic, serialized lane, never against a `dotnet test JobTrack.slnx` run
where other PostgreSQL-backed projects contend for the same local instance concurrently (that
contention is real — roughly 2-3x isolated latency — but it is a runner scheduling concern, not a
query regression, and widening a ceiling to absorb it defeats the ceiling's purpose):

```bash
./scripts/perf-test.sh
```

This cleans up any orphaned test databases, runs the performance project alone, then cleans up
again. Pass any `dotnet test` arguments through, e.g. `./scripts/perf-test.sh --filter
"FullyQualifiedName~FullTableHierarchyLoadPerformanceTests"`. A future ceiling increase needs a
before/after query plan and an explicit product-regression rationale — "the shared test server was
busy" is a reason to fix the runner, not to widen a query budget.

### Running everything

`dotnet test JobTrack.slnx` and `./scripts/perf-test.sh` cover different halves (the latter is
deliberately excluded from the former, see above); to run both in one command:

```bash
./scripts/all-test.sh
```

This is the full solution suite plus the performance lane, back to back, with orphaned-database
cleanup around each. Takes several minutes — occasional use only (end of a multi-stage plan, before
a substantial closing commit), not the per-commit gate. Any arguments are passed through to
`perf-test.sh` (and so on to its `dotnet test` invocation), e.g. `./scripts/all-test.sh --filter
"FullyQualifiedName~FullTableHierarchyLoadPerformanceTests"`.

### Console logging in test hosts

`tests/web-test-hosts.runsettings` sets `Logging__Console__LogLevel__Default=Warning` for
`JobTrack.Web.IntegrationTests` and `JobTrack.Web.EndToEndTests` (each wires it in via
`<RunSettingsFilePath>`), so a test run's output carries warnings and failures rather than a
per-request `api_request` line from `ApiTelemetryFilter` and an authentication line per rejected
bearer token. The end-to-end fixtures' child `dotnet JobTrack.Web.dll` processes inherit the
variable through their environment.

It is scoped to the console provider, not to `Logging:LogLevel`, because `ApiOperationalQualitiesTests`
and `SensitiveLoggingTests` attach their own capturing `ILoggerProvider` and assert on
Information-level entries — those two are what fails if it is ever widened. `appsettings.json` and
`appsettings.Development.json` are untouched, so a deployment and
`dotnet run --project src/JobTrack.Web` log exactly as before.

### Cleaning up orphaned test databases

Each database-contract test class creates a disposable, uniquely named PostgreSQL database and
SQLite file and drops/deletes it on teardown — but a killed or interrupted `dotnet test` run
(timeout, Ctrl-C, crashed sandboxed process) skips that teardown and leaves orphans behind. Run
this periodically, or whenever `dotnet test` was interrupted:

```bash
./scripts/clean-test-databases.sh
```

It drops any `jobtrack_test_*` PostgreSQL database on the local instance and deletes any
`jobtrack_test_*.db`/`.db-shm`/`.db-wal`/`.db-journal` file left in `$TMPDIR`.

The real-browser end-to-end suite (`tests/JobTrack.Web.EndToEndTests`) additionally requires the
Playwright browser binaries installed once per machine:

```bash
dotnet build tests/JobTrack.Web.EndToEndTests/JobTrack.Web.EndToEndTests.csproj
pwsh tests/JobTrack.Web.EndToEndTests/bin/Debug/net10.0/playwright.ps1 install chromium firefox webkit
```

## Running on a development server

The application's own default is PostgreSQL (`appsettings.json`); the steps below apply to either
provider, run from the repository root (`dotnet run --project` does not change the process's
working directory, so a relative SQLite path resolves from wherever you invoke it — keep the
`--connection-string` consistent across all three steps so they operate on the same database).

### PostgreSQL

1. **Confirm the server is up**, then create a database for JobTrack (any name; this uses the
   default one from your Homebrew/OS user — adjust the connection string in later steps if you use
   a different role or database name):

   ```bash
   pg_isready -h /tmp -p 5432
   psql -h /tmp -p 5432 -d postgres -c "CREATE DATABASE jobtrack_dev LOCALE_PROVIDER icu ICU_LOCALE 'en-GB' TEMPLATE template0"
   ```

2. **Deploy the schema** (`--scripts-root` is required — nothing copies `database/` next to the
   built binary). `JobTrack.Database deploy` automatically applies
   `database/postgresql/roles/jobtrack-roles-and-grants.sql` afterwards — no separate
   role-provisioning step is needed against a fresh cluster:

   ```bash
   dotnet run --project src/JobTrack.Database -- deploy --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_dev" --scripts-root database/postgresql/schema-versions
   ```

3. **Bootstrap the first administrator** (interactive — prompts for display name, IANA time zone,
   username, and password, then invokes the library's one-time atomic bootstrap command):

   ```bash
   dotnet run --project src/JobTrack.AdminCli -- bootstrap --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_dev"
   ```

   A direct `--connection-string` is accepted only when it contains no `Password`/`Pwd` property.
   Every `JobTrack.AdminCli`/`JobTrack.Database` command accepts `--connection-string-file <path>`
   (the file's trimmed contents); a PostgreSQL passfile or integrated authentication also keeps the
   database credential out of `argv`. `bootstrap`/`create-employee` accept `--password-stdin` (one
   line read from standard input, matching `docker login --password-stdin`) for automation. The
   removed `--password` flag is rejected; omitting `--password-stdin` falls back to the masked
   interactive prompt (security review remediation §2.7).

4. **Point the web app at that database and run it.** Either edit
   `src/JobTrack.Web/appsettings.Development.json` (`Database:Provider` → `PostgreSql`,
   `ConnectionStrings:JobTrackIdentity`, `ConnectionStrings:JobTrackDomain`,
   `ConnectionStrings:JobTrackPatManagement`, and `ConnectionStrings:JobTrackPatAuthentication` →
   the connection string above. Security review remediation §2.6 split the runtime credentials by
   capability; a local superuser connection satisfies all four, so the same connection string works
   for every key here) or set the equivalent environment variables, then:

   ```bash
   dotnet run --project src/JobTrack.Web
   ```

   This uses the `http` launch profile by default, listening on `http://localhost:5034`, and
   launches a browser automatically. Add `--launch-profile https` for
   `https://localhost:7174` (plus `http://localhost:5034`) instead — see
   `src/JobTrack.Web/Properties/launchSettings.json`. Sign in with the administrator credentials
   from step 3 — first sign-in forces an immediate password change.

   To start over, `dropdb -h /tmp -p 5432 jobtrack_dev` and repeat from step 1 — schema deployment
   is not idempotent against a database that already has JobTrack's tables.

### SQLite

`src/JobTrack.Web/appsettings.Development.json` ships pointed at a local SQLite file
(`jobtrack-web-dev.db`) by default, purely so a first local run needs no PostgreSQL setup or
connection-string editing:

1. **Deploy the schema** to a fresh SQLite file:

   ```bash
   dotnet run --project src/JobTrack.Database -- deploy --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --scripts-root database/sqlite/schema-versions
   ```

2. **Bootstrap the first administrator**:

   ```bash
   dotnet run --project src/JobTrack.AdminCli -- bootstrap --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db"
   ```

3. **Run the web app**:

   ```bash
   dotnet run --project src/JobTrack.Web
   ```

   Same launch profiles and first-sign-in behaviour as above.

`jobtrack-web-dev.db` (+ its `-shm`/`-wal` sidecar files) is gitignored and disposable — delete it
between unrelated manual runs so stale bootstrap/account state doesn't leak into the next one, then
repeat steps 1-2 before running again.

### Resetting a password

The normal way to reset an employee's password is the Administrator-only page in the web
interface. When that isn't usable — the administrator account itself is locked out, or the web
app isn't reachable — `JobTrack.AdminCli`'s `reset-password` command is the emergency fallback: it
talks to the database directly (in-process, not over HTTP) and works against either provider the
same way `bootstrap` does:

```bash
# PostgreSQL
dotnet run --project src/JobTrack.AdminCli -- reset-password --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_dev" --username <username>

# SQLite
dotnet run --project src/JobTrack.AdminCli -- reset-password --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --username <username>
```

It prints a one-time temporary password to relay to the employee out-of-band, forces a password
change at their next sign-in, and revokes every personal access token and session tied to that
account. See `src/JobTrack.AdminCli/EmergencyPasswordReset.cs` for exactly what it does and why.

### Resetting two-factor authentication

Two-factor authentication (TOTP, ADR 0037) is optional and self-service: an employee enrols and
disables it themselves from the web interface. If they lose their authenticator device, an
administrator can clear it for them from the Administrator-only account page, or — same fallback
as a password reset — `JobTrack.AdminCli`'s `reset-2fa` command works when the web app isn't
reachable, including for the administrator account itself:

```bash
# PostgreSQL
dotnet run --project src/JobTrack.AdminCli -- reset-2fa --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_dev" --username <username>

# SQLite
dotnet run --project src/JobTrack.AdminCli -- reset-2fa --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --username <username>
```

It clears the account's two-factor enrolment (the employee can then sign in with their password
alone and re-enrol if they choose), revokes every personal access token and session tied to that
account, and audits the operation. See `src/JobTrack.AdminCli/EmergencyTwoFactorReset.cs`.

### Bulk-generating a tree of job nodes from JSON

`JobTrack.AdminCli`'s `import-tree` command atomically creates a whole job-node subtree from a flat
JSON array — optionally including work already done against each leaf — in one database transaction.
`samples/job-tree-imports/` has seven worked examples, from 5 to 30 nodes.
[`operations/job-tree-import.md`](operations/job-tree-import.md) is the command and file-format
reference.

```bash
dotnet run --project src/JobTrack.AdminCli -- import-tree --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --username <username> --file samples/job-tree-imports/building-a-house.json
```

A file may flag one node `"home": true` (as `building-a-house.json` does), making it the home node of
the importing employee and of anyone named in `--home-node-for` — the node they land on after login,
and the default scope of the header's Jobs and Awaiting-progress links. That is how the Docker image
seeds `admin` and `demo` onto "Build a house" rather than the bare root.

### Creating employees and issuing API tokens from the CLI

Three further commands exist for scripted setup, where the web interface is the normal route:

- `create-employee` provisions a non-administrator employee under an existing administrator
  (`--actor`), granting `--roles` (first entry as the initial role, the rest assigned after).
  `--no-force-password-change` clears the ADR 0023 forced-change flag, for a deliberately shared
  credential such as the container demo's `demo` account. The new account's password satisfies
  `PasswordPolicy` (15+ characters, not blocklisted; ADR 0056) without an operational bypass.
  Omitting `--password-stdin` (like `bootstrap`) prompts interactively without echo; automation
  passes one line through standard input. Plaintext password arguments are rejected.

```bash
printf '%s\n' "$INITIAL_PASSWORD" | dotnet run --project src/JobTrack.AdminCli -- create-employee --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --actor <admin-username> --username <username> --password-stdin --display-name <name> --roles Worker
```

- `set-home-node` points an existing employee's post-login landing node at a branch
  (`--node-id`), or clears it back to the tree root (`--clear`). The preference is self-service in
  the application layer, so the command runs *as* the named employee — there is no `--actor`.

```bash
dotnet run --project src/JobTrack.AdminCli -- set-home-node --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --username <username> --node-id 42
```

- `set-schedule` makes one uniform weekly pattern an employee's standing rota — a single civil-time
  interval repeated across `--days`. It **corrects rather than adds**, which is the point: every
  account is created with `EmployeeProvisioningDefaults`' Mon–Fri 09:00–17:00 from 2020-01-01,
  open-ended, so a plain add always collides on the `schedule-version-overlap` invariant. On a freshly
  provisioned account the intent is to replace that placeholder, not to record a change of working
  pattern, so the existing version is corrected in place (ADR 0003). It keeps that version's effective
  start unless `--effective-start` is given, so existing sessions stay inside covered working time,
  and refuses outright once the employee has more than one version or any schedule exception — real
  history belongs in the Rota pages, where you can see what you are changing. A per-day pattern, an
  effective end, or an exception is likewise the Rota pages' job, not this command's.

```bash
dotnet run --project src/JobTrack.AdminCli -- set-schedule --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --actor <admin-username> --username <username> --days Mon,Tue,Wed,Thu,Fri --start 09:00 --end 17:00
```

A personal access token can only be issued through the self-service `/Account/PersonalAccessTokens`
page by the signed-in owner (ADR 0029, ADR 0055) — there is no CLI or unauthenticated path to mint
one for another account.

Run `JobTrack.AdminCli` with no arguments for the full option list of every command.

## Seeding a synthetic end-user testing (UAT) scenario

`samples/JobTrack.UatSeed` seeds a canonical, non-PII scenario on top of an already-deployed,
already-bootstrapped database (steps 1-3 above) — a requester, the six operational roles, a
holding area, an unassigned request, acknowledged/assigned work, a prerequisite blocker, an active
work session, and finished cost-reportable work with a rate applied, so a human tester (staff or
requester) has real state to explore immediately rather than an empty bootstrap admin account. It
runs through `IJobTrackClient` throughout, the same public surface a real client uses — the
department and holding-area rows are the only direct SQL, because no library command exists yet to
configure them (see
[`plans/2026-07-11-client-requester-intake-plan.md`](plans/2026-07-11-client-requester-intake-plan.md)'s
deferred "holding-area admin UI" follow-up). Every seeded employee's password is
`Uat-Seed-Battery-42!` and forces a change at first sign-in; run it against SQLite/PostgreSQL
exactly as in "Running on a development server" above:

```bash
# SQLite, after deploy + bootstrap against jobtrack-web-dev.db:
dotnet run --project samples/JobTrack.UatSeed -- --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db"

# PostgreSQL, after deploy + bootstrap against jobtrack_dev:
dotnet run --project samples/JobTrack.UatSeed -- --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_dev"
```

The command prints every seeded user's id/username and the seeded job node ids so a tester can jump
straight to interesting state (an unassigned request to triage, a blocked leaf, an open session).

**Resetting between UAT rounds** — the seed is not idempotent (re-running it against the same
database adds a second copy of everything), so start from a fresh database each round:

- **SQLite:** delete `jobtrack-web-dev.db` (+ `-shm`/`-wal`) and repeat deploy → bootstrap → seed.
- **PostgreSQL:** `dropdb -h /tmp -p 5432 jobtrack_dev` (or `DROP DATABASE "jobtrack_dev" WITH (FORCE);`
  via `psql`), then `CREATE DATABASE` and repeat deploy → bootstrap → seed exactly as in "Running on
  a development server" above.

## Text storage and Unicode

Both providers store text as their platform default (PostgreSQL's `UTF8` database encoding;
SQLite's `UTF-8` text encoding), via unconstrained `text` columns — neither is pinned explicitly in
connection setup, and no length caps live at the schema level. .NET strings are UTF-16 internally,
so any length check written as `string.Length` counts UTF-16 code units, not Unicode codepoints —
a character outside the Basic Multilingual Plane (e.g. most emoji) is a surrogate pair and counts as
2. Most length validation in this codebase (`[MaxLength(n)]` on Razor Page models, e.g.
`Requests/Details.cshtml.cs`, `Requests/Index.cshtml.cs`, `Account/PersonalAccessTokens.cshtml.cs`)
is `.Length`-based and inherits that behaviour. The one deliberate exception is the password policy
(`JobTrack.Abstractions/PasswordPolicy.cs`, ADR 0056): its 15–128 length bound is counted via
`password.EnumerateRunes().Count()` — Unicode scalar values — specifically so a password containing
surrogate-pair characters isn't double-counted. Job-description search is also Unicode-aware:
PostgreSQL's native `lower()` handles it, while SQLite's built-in `lower()` is ASCII-only, so
`JobTrack.Persistence.Sqlite` registers a custom deterministic case-folding function instead (ADR
0050).

## Codebase size

The line count is dominated by the test suite, not the application. At the time of writing the
`src/` tree (the whole app — dual-provider persistence, costing domain, ASP.NET Identity, the
external HTTP API, and the admin CLI) is ~24.8k lines of C# plus ~3.1k of Razor, against ~52.2k
lines under `tests/` — roughly a 2:1 test-to-source ratio. That ratio is a property of the mandatory
TDD discipline (see [`CLAUDE.md`](../CLAUDE.md)), not accumulated fat: the tests are a deliverable,
so a large `tests/` total is a sign of coverage, not bloat. Recalculate the split with
[`tokei`](https://github.com/XAMPPRocky/tokei):

```bash
tokei src      # application/library code
tokei tests    # test suite
tokei          # whole repo, all languages
```

## Project layout

See [`architecture-overview.md`](architecture-overview.md) for a file-by-file table of each layer
(database, reusable library, HTTP API, web site) plus `spikes/` and `samples/`.

```
src/
  JobTrack.Abstractions            identifiers, value types, exception hierarchy — no deps
  JobTrack.Domain                  pure cost engine, interval algebra, achievement, rates
  JobTrack.Application             IJobTrackClient facade, commands/queries, auth, audit
  JobTrack.Persistence.PostgreSql  EF Core + Npgsql implementation
  JobTrack.Persistence.Shared      internal EF model config shared by both providers
  JobTrack.Persistence.Sqlite      EF Core + Sqlite implementation
  JobTrack.Identity                ASP.NET Core Identity adapter
  JobTrack.Database                schema deployment tool (no EF/domain dependency)
  JobTrack.AdminCli                bootstrap/reset CLI
  JobTrack.Web                     Razor Pages host + external HTTP API (/api/*)
samples/
  JobTrack.Sample.PostgreSql       minimal in-process IJobTrackClient consumer (PostgreSQL)
  JobTrack.Sample.Sqlite           minimal in-process IJobTrackClient consumer (SQLite)
  JobTrack.ExternalApiClient       first-party HTTP client proof — no JobTrack.* library reference
  JobTrack.UatSeed                 synthetic end-user-testing scenario seeder
  job-tree-imports/                worked JSON examples for AdminCli `import-tree`
tests/                             one test project per src project, plus
                                    JobTrack.ArchitectureTests, JobTrack.Database.ContractTests,
                                    JobTrack.PublicApi.Tests, JobTrack.Web.{IntegrationTests,EndToEndTests}
database/{postgresql,sqlite,scenarios}/   schema-versions, reference data, verification per provider
docs/{decisions,operations,plans,traceability}/
```
