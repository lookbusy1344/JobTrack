# ADR 0064: Provider-neutral port bodies live in `JobTrack.Persistence.Shared`

**Status:** Accepted
**Amends:** ADR 0049's `InternalsVisibleTo` list (extended 2026-07-26 section) — `JobTrack.Application`
now grants internals access to `JobTrack.Persistence.Shared` as well as the two providers.
**Amends:** ADR 0010/impl plan §7.4's framing of `JobTrack.Persistence.Shared` as *EF model
configuration only*. Its anti-drift purpose is unchanged; its remit widens.

## Context

`JobTrack.Persistence.PostgreSql` (5,861 lines) and `JobTrack.Persistence.Sqlite` (5,913) together
were 31% of `src/`, and the matched port pairs within them were near-verbatim copies. Normalising the
provider name prefix out and diffing:

| Port pair | pair total | differing |
|---|---|---|
| `JobBrowseQueryPort` | 699 | 27 (4%) |
| `EmployeeCommandPort` | 951 | 75 (8%) |
| `ScheduleCommandPort` | 837 | 79 (9%) |
| `RateCommandPort` | 719 | 81 (11%) |
| `JobRequestCommandPort` | 1,020 | 122 (12%) |
| `WorkSessionCommandPort` | 1,992 | 232 (12%) |
| `JobNodeCommandPort` | 2,439 | 307 (13%) |
| `CostQueryPort` | 881 | 249 (28%) |
| `AwaitingProgressQueryPort` | 673 | 255 (38%) |

The differences in the well-matched pairs are mechanical, not semantic: how a context is opened
(`NpgsqlDataSource` versus a connection string needing `SqliteConnectionPragmas` applied per
connection), which isolation level starts a write (PostgreSQL's advisory lock versus SQLite's
`BEGIN IMMEDIATE` single-writer stand-in), and a handful of provider-bound SQL mechanisms. The
LINQ/EF bodies either side of those seams were identical.

That duplication is itself the risk `JobTrack.Persistence.Shared` exists to prevent. Its README
states its purpose as keeping the providers from drifting "independently of each other or of the
reviewed SQL schema", yet the largest and most behaviour-bearing files were held in lockstep only by
convention and by running two conformance suites.

The blocker was structural, not architectural. `Persistence.Shared` referenced `JobTrack.Abstractions`
alone, so it could not name an `Application` type — meaning it could not implement an
`Application.Ports.I*` interface or traffic in port records. Two files already carried a comment
recording exactly this: `SubtreeImpactProjection` was "duplicated per provider on purpose … so it
cannot name an `Application` type", despite the two copies being byte-identical.

## Decision

`JobTrack.Persistence.Shared` references `JobTrack.Application` (and `JobTrack.Domain` transitively)
and hosts the provider-neutral body of each well-matched port under `Ports/`.

**Composition, not inheritance.** Each shared port is a `sealed` class taking a provider-operations
interface:

```csharp
internal sealed class JobBrowseQueryPort(IJobBrowseProviderOperations provider) : IJobBrowseQueryPort
```

`IJobBrowseProviderOperations` declares only what cannot be expressed provider-neutrally — for
browse, exactly two members: `CreateContext()` and `IsSubtreeSucceededAsync(...)`. Each provider ships
a small `*Operations` class implementing it, and the composition roots (`JobTrackPostgreSql.Create`,
`JobTrackSqlite.Create`) wire the two together.

Layering is unchanged and still a DAG in the mandated direction:
`Abstractions ← Domain ← Application ← Persistence.Shared ← {PostgreSql, Sqlite}`. Everything in
`Persistence.Shared` remains `internal`; both `PublicAPI` files stay empty.

### Conflict classification is one closed enum, not a predicate per constraint

`WorkSessionCommandPort` alone had five per-provider `Find*Violation` helpers, each walking the
exception chain for one constraint, disambiguated at the call site by `catch` order. Reproducing that
as five seam members would have made the seam mostly error-plumbing.

Instead `IProviderWriteOperations.ClassifyWriteConflict` returns one `WriteConflictKind`, and a call
site catches the kinds its own operation can provoke, in its own order of specificity. The enum is
named for the constraint the driver reported, never for what a call site makes of it — that is what
lets one classifier serve the rate, schedule and work-session ports at once, because the same
PostgreSQL `23505` is an overlap to a rate write and "this worker is already active" to a session
write. Each provider's implementation walks the chain once and returns the most specific kind found
anywhere in it, which is exactly what the separate whole-chain walks used to compute.

The one behaviour this deliberately does *not* preserve is a latent SQLite-only quirk: because
`SQLITE_CONSTRAINT` is a single base code, SQLite's broad overlap filter would also have caught an
active-sessions trigger at `CorrectSessionAsync` and mislabelled it as an overlap, where PostgreSQL's
distinct SQLSTATE let it propagate. The shared body now matches PostgreSQL. That divergence existed
only because the two copies drifted.

### Immediate versus deferred triggers are an explicit seam, not an ordering accident

`CompleteLeafAsync` was the one method whose *write order* genuinely differed. SQLite's
`leaf-closure-active-sessions` trigger is immediate, so the session-finish rows must reach the table
before the achievement `UPDATE` or the trigger still sees an active session; PostgreSQL's equivalent
is deferred to commit, by which time both writes are present.
`IProviderWriteOperations.FlushBeforeTerminalTransitionAsync` names that requirement — an extra
`SaveChangesAsync` inside the one open transaction on SQLite, nothing at all on PostgreSQL rather
than a wasted round trip. Atomicity is unchanged either way: a failure on either call still rolls the
whole transaction back.

Prerequisite readiness is seamed the same way (`IsLeafReadyAsync`): the decision is identical, but
PostgreSQL takes ADR 0012's per-leaf advisory lock for each required job and SQLite's single-writer
transaction takes none. `LeafReadiness` therefore stays per-provider, since the un-converted
job-node and achievement ports still call it directly.

### What stays duplicated

`CostQueryPort` and `AwaitingProgressQueryPort` keep two independent implementations. Their
divergence (28% and 38%) is genuine provider-specific SQL, and PostgreSQL is the performance-relevant
target for both. Forcing a seam there would need a wide operations interface whose members exist only
to paper over real differences — trading readable divergence for a leaky abstraction, and
constraining the provider whose query plans actually matter.

`JobNodeCommandPort` and `JobRequestCommandPort` keep two implementations for a different reason:
their line-level diff understates them. PostgreSQL routes move and decompose through stored functions
(`move_job_node`) whose cycle and compare-and-swap checks are deferred to commit inside the function's
own schema, while SQLite performs the same work inline against immediate triggers and a `CHECK`
constraint. That is a different algorithm, not different plumbing, and unifying it would mean seaming
whole method bodies rather than named operations.

Raw SQL stays in the provider projects. `InlineDmlArchitectureTests` scans only the two provider
directories, and nothing in this decision moves a SQL string into `Persistence.Shared`.

## Rationale

- **Composition over inheritance was chosen deliberately.** A `protected abstract` template method
  would have saved the same lines, but provider behaviour would only be discoverable by reading a
  base class and a derived class together and reconstructing the call graph. An explicit interface
  makes the seam *enumerable*: one small file is the complete answer to "how does PostgreSQL differ
  from SQLite for this port?", and adding a member to it is a visible act that wants justifying.
  Before this ADR that question could only be answered by diffing ~700 lines.
- **Widening `Persistence.Shared` beat creating a new project.** A separate
  `JobTrack.Persistence.Ports` assembly would have preserved the letter of Shared's narrow dependency
  boundary at the cost of a fourth packaged persistence assembly, a second README, `PublicAPI`
  baselines and lock file. Shared already hosted port-level *behaviour* (`SubtreeDeletionCascade`,
  `UnassignedNodeClaim`, `WriteUpChangeApplier`, `SessionEndClipping`), so this extends an existing
  remit rather than inventing one.
- **Doing nothing was rejected** because the duplication defers its cost to whoever patches one copy
  and not the other — the precise failure mode Shared was created to prevent.

## Consequences

- `JobTrack.Persistence.Shared` recompiles when an `Application` port changes; it was previously
  insulated. It must still never reference a provider assembly or ASP.NET Core.
- `ReusableLibraryDependencyTests.AllowedProjectReferences` widens Shared's entry to
  `{Abstractions, Domain, Application}`. The `BeSubsetOf` assertion and the
  `Shared_persistence_configuration_preserves_its_narrow_dependency_boundary` case still run — the
  boundary moved by decision, it was not removed.
- `JobTrack.Application` grants `InternalsVisibleTo` to `JobTrack.Persistence.Shared`. ADR 0049's
  guarantee is preserved in substance: Shared is provider infrastructure, not a new consumer, and no
  additional assembly outside this solution gains anything. `Persistence.Shared` in turn grants
  internals access to the two provider test projects and `JobTrack.Database.PerformanceTests`, which
  construct the shared bodies over their own provider operations exactly as the composition roots do.
- **The measured saving is smaller than a naive reading of the table above suggests.** "86% identical"
  describes how much of *each copy* is redundant; deleting one duplicate can at best halve a pair, and
  the seam costs real lines. The browse pair went 785 → 565 (−28%) once the two byte-identical
  projection files moved too. Expect roughly 40% per converted pair, not 86%.
- A converted port's per-provider conformance suite is unchanged and still runs in full against both
  providers. The interceptor-based round-trip guards (ADR 0039, Stage 6) continue to assert command
  counts, so a shared body cannot silently change query shape for either provider.
- Provider file naming changes for converted ports: `PostgreSqlJobBrowseQueryPort.cs` becomes
  `PostgreSqlJobBrowseOperations.cs`. `InlineDmlArchitectureTests`' raw-SQL inventory pins file path
  and enclosing method name, so converting a port with raw SQL requires updating its inventory entry.
