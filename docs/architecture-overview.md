# Architecture overview

A file-level map of the codebase, grouped by the four architectural layers in
[`CLAUDE.md`](../CLAUDE.md)'s mandatory implementation order: database → reusable library →
external HTTP API → web site. Both the HTTP API and the web site are hosted by the same
`JobTrack.Web` process, but neither ever reaches the database directly — both call
`IJobTrackClient` only. Short sections on `spikes/` and `samples/` follow. See
[`docs/jobtrack_spec_codex.md`](jobtrack_spec_codex.md) for the normative spec and
[`docs/database-entities.md`](database-entities.md) for the entity/costing model.

## 1. Database

Numbered, forward-only SQL DDL, one script per schema version, applied by `JobTrack.Database`.

| Path | Contents |
|---|---|
| [`database/postgresql/schema-versions/`](../database/postgresql/schema-versions/) | PostgreSQL DDL scripts 0001–0020, checksummed and applied in order. |
| [`database/postgresql/reference-data/`](../database/postgresql/reference-data/) | PostgreSQL static/reference seed data (slot, currently empty). |
| [`database/postgresql/roles/jobtrack-roles-and-grants.sql`](../database/postgresql/roles/jobtrack-roles-and-grants.sql) | Idempotent, non-versioned role/privilege separation — the app role can't do DDL or erase audit history. |
| [`database/postgresql/verification/`](../database/postgresql/verification/) | PostgreSQL post-deploy schema verification scripts (slot, currently empty). |
| [`database/sqlite/schema-versions/`](../database/sqlite/schema-versions/) | SQLite DDL scripts 0001–0014, mirroring the PostgreSQL version sequence. |
| [`database/sqlite/reference-data/`](../database/sqlite/reference-data/) | SQLite static/reference seed data (slot, currently empty). |
| [`database/sqlite/verification/`](../database/sqlite/verification/) | SQLite post-deploy schema verification scripts (slot, currently empty). |
| [`database/scenarios/README.md`](../database/scenarios/README.md) | Notes this is a reserved slot; actual golden/generated test scenarios live as code in `tests/JobTrack.TestSupport` and `tests/JobTrack.Database.ContractTests`, not here. |

## 2. Reusable library

Provider-agnostic domain and application logic, plus the two EF Core persistence providers. This
is the layer under public-API compatibility discipline (impl plan §7.5).

| Path | Contents |
|---|---|
| [`src/JobTrack.Abstractions/`](../src/JobTrack.Abstractions/) | Strongly typed IDs (`JobNodeId`, `AppUserId`, ...), shared value types (`Money`, `HourlyRate`), enums, and the public `JobTrackException` hierarchy — zero provider/framework dependency. |
| [`src/JobTrack.Domain/`](../src/JobTrack.Domain/) | Pure, immutable domain model, no I/O: `Authorization/` (access policies), `Costing/` (the cost engine), `Hierarchy/` (achievement/awaiting-progress calculators), `Intervals/` (interval algebra), `Rates/` (rate resolution), `Schedules/` (civil-time/schedule-exception resolution). |
| [`src/JobTrack.Application/`](../src/JobTrack.Application/) | The `IJobTrackClient` facade plus command/query request/result records and handlers (`JobCommands.cs`, `JobQueries.cs`, `RateCommands.cs`, `TokenCommands.cs`, ...); `Ports/` holds the persistence-port interfaces the two providers implement. |
| [`src/JobTrack.Persistence.Shared/`](../src/JobTrack.Persistence.Shared/) | EF Core model configuration shared by both providers — entity mappings, ID converters, concurrency tokens — so PostgreSQL and SQLite can't drift apart. |
| [`src/JobTrack.Persistence.PostgreSql/`](../src/JobTrack.Persistence.PostgreSql/) | PostgreSQL implementation of the Application ports via EF Core/Npgsql: one `PostgreSql*Port.cs` per port, plus `JobTrackPostgreSql.cs` (public `Create` entry point) and `PostgreSqlJobTrackDbContext.cs`. |
| [`src/JobTrack.Persistence.Sqlite/`](../src/JobTrack.Persistence.Sqlite/) | SQLite implementation of the same ports via EF Core, full parity with PostgreSQL: `Sqlite*Port.cs` files plus `JobTrackSqlite.cs` entry point and `SqliteJobTrackDbContext.cs`. |
| [`src/JobTrack.Identity/`](../src/JobTrack.Identity/) | ASP.NET Core Identity adapter (production `DbContext`s for both providers, `JobTrackUserStore`, claims-principal factory, TOTP support); composed only by Web and AdminCli, not part of the public library surface. |
| [`src/JobTrack.Database/`](../src/JobTrack.Database/) | Standalone schema-deployment tool (`Program.cs`): applies ordered schema-version scripts with checksum validation, plus PostgreSQL roles/grants and deployment-lock strategies for both providers. |
| [`src/JobTrack.AdminCli/`](../src/JobTrack.AdminCli/) | Narrow admin CLI host (`Program.cs`): bootstrap admin, create employee, issue token, emergency password/2FA reset, job-tree import — thin wrappers over library commands. |

## 3. External HTTP API

Hosted inside [`src/JobTrack.Web`](../src/JobTrack.Web/), alongside the web site, but a distinct
route surface (`/api/*`).

| Path | Contents |
|---|---|
| [`src/JobTrack.Web/JobTrackApi.cs`](../src/JobTrack.Web/JobTrackApi.cs) | The entire minimal-API HTTP surface — route group with `MapGet`/`MapPost`/`MapPut`/`MapDelete` endpoints for jobs, sessions, rates, prerequisites, cost, schedule, etc.; registered from `Program.cs` via `app.MapJobTrackApi()`. |
| [`src/JobTrack.Web/BearerSecuritySchemeTransformer.cs`](../src/JobTrack.Web/BearerSecuritySchemeTransformer.cs) | OpenAPI document transformer adding the bearer/PAT security scheme to the API's OpenAPI description. |
| [`src/JobTrack.Web/PersonalAccessTokenAuthentication.cs`](../src/JobTrack.Web/PersonalAccessTokenAuthentication.cs) | Authentication handler validating personal-access-token bearer credentials for external API clients. |
| [`src/JobTrack.Web/RequiresPasswordChangeEndpointFilter.cs`](../src/JobTrack.Web/RequiresPasswordChangeEndpointFilter.cs) | Minimal-API endpoint filter blocking API calls from accounts pending a forced password change. |

See [`docs/plans/2026-07-09-external-http-api-plan.md`](plans/2026-07-09-external-http-api-plan.md)
for the API's client trust model, auth, and exposure scope.

## 4. Web site

Also hosted inside [`src/JobTrack.Web`](../src/JobTrack.Web/) — Razor Pages, following ADR 0044's
navigation philosophy.

| Path | Contents |
|---|---|
| [`src/JobTrack.Web/Pages/Account/`](../src/JobTrack.Web/Pages/Account/) | Login, two-factor login/management, password change, personal-access-token self-service. |
| [`src/JobTrack.Web/Pages/Admin/`](../src/JobTrack.Web/Pages/Admin/) | Role assignment, rate/rate-override correction, employee-account management. |
| [`src/JobTrack.Web/Pages/Jobs/`](../src/JobTrack.Web/Pages/Jobs/) | Browse, create/edit/delete/move job nodes, decompose/achieve leaves, work sessions, prerequisites, cost reports. |
| [`src/JobTrack.Web/Pages/Requests/`](../src/JobTrack.Web/Pages/Requests/) | List and view job-request (intake) details. |
| [`src/JobTrack.Web/Pages/Rota/`](../src/JobTrack.Web/Pages/Rota/) | Schedule/rota view and correcting schedule versions/exceptions. |
| [`src/JobTrack.Web/Pages/Audit/`](../src/JobTrack.Web/Pages/Audit/) | Browsing the audit-event log. |
| [`src/JobTrack.Web/Pages/Shared/`](../src/JobTrack.Web/Pages/Shared/) | Shared `_Layout.cshtml` and partials (icons, backdate forms, work-row actions, write-up field). |
| [`src/JobTrack.Web/Pages/Index.cshtml`](../src/JobTrack.Web/Pages/Index.cshtml), [`Error.cshtml`](../src/JobTrack.Web/Pages/Error.cshtml) | Home and error pages. |
| [`src/JobTrack.Web/Program.cs`](../src/JobTrack.Web/Program.cs) | Host composition root: DI registration, auth/identity setup, rate limiting, endpoint/page mapping (`MapJobTrackApi()` + `MapRazorPages()`). |
| `src/JobTrack.Web/*Model.cs`, `*Display.cs` (top level) | View/display helper types (`JobNodeDisplay`, `MoneyDisplay`, `InstantDisplay`, `WorkRowActionsModel`, ...) shared across pages for presentation formatting. |
| [`src/JobTrack.Web/wwwroot/`](../src/JobTrack.Web/wwwroot/) | Static assets — `css/site.css`, `js/site.js`, `js/job-history.js`, pinned third-party `lib/` (Bootstrap, Mulish font), favicon. |
| [`src/JobTrack.Web/Properties/launchSettings.json`](../src/JobTrack.Web/Properties/launchSettings.json) | Local run/launch profile configuration. |

See [`docs/design-language.md`](design-language.md) for the "Console" visual design system.

## Spikes

Throwaway, pre-Phase-0 proof-of-concept code that de-risks a design decision — not part of
production code or delivery gates. Write-ups live in
[`docs/traceability/spike-report.md`](traceability/spike-report.md).

| Path | Contents |
|---|---|
| [`spikes/cost-sweep-spike/`](../spikes/cost-sweep-spike/) | .NET console spike exploring the cost-sweep/allocation algorithm design. |
| [`spikes/dst-spike/`](../spikes/dst-spike/) | .NET console spike prototyping the deterministic-simulation-testing (DST) approach. |
| [`spikes/sql/`](../spikes/sql/) | Standalone PostgreSQL SQL spikes (numbered 01–05) plus shell scripts for concurrent testing — single-root locking, prerequisite cycles, GiST overlap exclusion, advisory-lock ordering, ltree hierarchy. |

## Samples

First-party consumers of the library and HTTP API, used as usage proof and dev tooling — not
part of the shipped product.

| Path | Contents |
|---|---|
| [`samples/JobTrack.ExternalApiClient/`](../samples/JobTrack.ExternalApiClient/) | Console app calling the JobTrack HTTP API over the network with a bearer token — the first-party client proof that the API is usable with zero `JobTrack.*` library references. |
| [`samples/JobTrack.Sample.PostgreSql/`](../samples/JobTrack.Sample.PostgreSql/) | Minimal smoke-test console app showing `JobTrackPostgreSql.Create` used directly (in-process, no HTTP). |
| [`samples/JobTrack.Sample.Sqlite/`](../samples/JobTrack.Sample.Sqlite/) | Minimal smoke-test console app showing `JobTrackSqlite.Create` used directly (in-process, no HTTP). |
| [`samples/JobTrack.UatSeed/`](../samples/JobTrack.UatSeed/) | Dev-only console tool seeding a realistic synthetic dataset (requester, roles, holding area, work, prerequisites, sessions, audit history) through `IJobTrackClient`, for end-user/UAT testing. |
| [`samples/job-tree-imports/`](../samples/job-tree-imports/) | Example JSON job-tree files (e.g. `building-a-house.json`, `farming-a-field.json`) consumed by AdminCli's `import-tree` command. |
