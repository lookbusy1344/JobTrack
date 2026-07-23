# ADR 0049: Internalize application ports, port-only records, and concrete command/query handlers

**Status:** Accepted
**Closes:** fresh-eyes architecture boundary remediation plan (`docs/plans/2026-07-23-architecture-boundary-remediation-plan.md`)
§2.3.

## Context

The documented, supported entry point into the reusable library is `IJobTrackClient`. Before this
ADR, `JobTrack.Application`'s persistence ports (`Ports/I*.cs`), the request/result records those
ports alone traffic in, and the concrete handler classes (`WorkCommands`, `JobCommands`,
`EmployeeCommands`, `AccountCredentialCommands`, `ScheduleCommands`, `RateCommands`, `CostQueries`,
`AuditQueries`, `TokenCommands`, `RequestCommands`, `AuthenticationAuditCommands`, `JobQueries`,
`InstallationCommands`) were all `public`, even though only the two persistence providers
(`JobTrack.Persistence.PostgreSql`/`Sqlite`) ever constructed them — `JobTrackClient` itself, the
`IJobTrackClient` implementation those handlers are wired into, was already `internal`.

A consumer could construct a handler directly (`new WorkCommands(myOwnPort, ...)`), passing a
hand-rolled `IWorkSessionCommandPort` that skips the authorization/transaction/audit obligations the
two real providers observe, and get something that type-checks as a working command surface. Nothing
enforced that a `IJobTrackClient` in the wild was actually backed by one of the two reviewed,
conformance-tested providers. `InternalsVisibleTo` from `JobTrack.Application` to both providers was
already in place, so nothing about the provider composition roots (`JobTrackPostgreSql.Create`/
`JobTrackSqlite.Create`) depended on this surface being public.

## Decision

Internalize the three categories the remediation plan named, keeping the facade unaffected:

- **Ports** (`JobTrack.Application.Ports.I*`) — `internal interface`.
- **Port-only records** — the request/result types used exclusively by those ports and their
  implementations (e.g. `AccountStateQueryResult`, `BootstrapPersistenceRequest`,
  `CreateEmployeePersistenceRequest`, `WorkerCostInputs`) — `internal sealed record`. The public
  request/result contracts `IJobTrackClient`'s facade methods actually accept and return (e.g.
  `CreateJobNodeRequest`, `AccountStateResult`, `CompleteLeafResult`) are unaffected; concrete
  handlers adapt between the two shapes.
- **Concrete handlers** — `internal sealed class`, matching `JobTrackClient`'s own existing
  visibility.

The thirteen public facade interfaces (`IWorkCommands`, `IJobCommands`, `IEmployeeCommands`,
`IAccountCredentialCommands`, `IScheduleCommands`, `IRateCommands`, `ICostQueries`, `IAuditQueries`,
`ITokenCommands`, `IRequestCommands`, `IAuthenticationAuditCommands`, `IJobQueries`,
`IInstallationCommands`) and `IJobTrackClient` itself stay public — those are what
`IJobTrackClient`'s own properties expose, and what `JobTrack.Web`/`JobTrack.AdminCli`/the samples
actually consume.

`JobTrack.Application.csproj` grants `InternalsVisibleTo` to exactly the assemblies that construct
these now-internal types directly: the two providers (already present), and the test projects that
exercise ports/handlers as part of provider-conformance or application-slice testing
(`JobTrack.Application.Tests`, `JobTrack.TestSupport`, `JobTrack.Persistence.PostgreSql.Tests`,
`JobTrack.Persistence.Sqlite.Tests`, `JobTrack.Database.PerformanceTests`,
`JobTrack.ArchitectureTests`).

`JobTrack.TestSupport`'s shared contract-test base classes (`WorkSessionCommandPortContractTestsBase`
and its siblings) stay `public` — xUnit's own analyzer (`xUnit1000`) requires a test class to be
public even when, as here, it is never run directly (`IsTestProject=false`; only its two provider
subclasses run it) — but their `Create*Port` abstract members, whose return types are now internal
port interfaces, are narrowed from `protected abstract` to `internal abstract`. `JobTrack.TestSupport`
grants `InternalsVisibleTo` to the two provider test projects that override them.

## Rationale

- Preferring internalization over documenting a supported provider-extension SPI matches the plan's
  own default: third-party persistence providers are not a real product requirement here, and every
  actual "port" implementation the project ships is already provider-internal.
- Reusing the *exact same* pattern `JobTrackClient` itself already used (public facade interface,
  internal implementation, providers as the only friend assemblies) keeps this a mechanical extension
  of an established convention rather than a new one.
- Narrowing the contract-test base classes' abstract members to `internal` (rather than, say,
  widening the ports back to `public`, or duplicating each base class per provider) keeps the shared
  contract-test pattern intact with the smallest possible surface change: the base classes are still
  reused verbatim by both providers, just with an accessibility level that matches what they actually
  return.

## Consequences

- Every `Ports/I*.cs` interface and its port-only records, and every concrete `*Commands`/`*Queries`
  handler in `JobTrack.Application`, are `internal`. `PublicAPI.Unshipped.txt` is updated to drop the
  now-internal members — a deliberate pre-release breaking change (`PublicAPI.Shipped.txt` is still
  empty; nothing has shipped).
- `JobTrack.Application.csproj` and `JobTrack.TestSupport.csproj` each gain the narrow
  `InternalsVisibleTo` list described above.
- A consumer outside this solution can no longer construct a working alternative `IJobTrackClient`
  implementation by hand-assembling ports and handlers; `JobTrackPostgreSql.Create`/
  `JobTrackSqlite.Create` are the only supported construction paths, as already documented.
- A public-surface architecture test (`tests/JobTrack.ArchitectureTests`) asserts
  `JobTrack.Application`'s public surface contains only the approved facade contracts (interfaces and
  their request/result records) plus the two provider assemblies' own public `JobTrackPostgreSql`/
  `JobTrackSqlite` factory types — regressing this internalization (e.g. a future PR making a handler
  public again) fails that test.
