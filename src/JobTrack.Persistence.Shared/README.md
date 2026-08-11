# JobTrack.Persistence.Shared

Everything `JobTrack.Persistence.PostgreSql` and `JobTrack.Persistence.Sqlite` hold in common, so the two providers cannot drift independently of each
other or of the reviewed SQL schema (impl plan §7.4, ADR 0010, ADR 0064):

- **EF Core model configuration** — entity mappings, strongly typed identifier converters, concurrency-token setup.
- **Provider-neutral port bodies** (`Ports/`) — the LINQ/EF implementation of an
  `Application.Ports.I*` port, `sealed`, taking a small `I*ProviderOperations` interface for the parts that genuinely cannot be written
  provider-neutrally. That interface is the complete, enumerable list of what differs between PostgreSQL and SQLite for its port; read it to know the
  whole provider-specific surface. Adding a member to one is a new divergence and wants justifying.
- **Shared write-path behaviour** — transaction/exception translation, audit event writing, and the cascade/claim/clipping helpers both providers
  invoke.

`CostQueryPort` and `AwaitingProgressQueryPort` are deliberately *not* shared: their provider differences are real SQL, not boilerplate (ADR 0064).
Raw SQL belongs in the provider projects, which are what `InlineDmlArchitectureTests` scans.

Every type here is internal; nothing is part of the public library surface, and both `PublicAPI`
files are held empty so a type that silently became public fails the build. This assembly may reference `JobTrack.Abstractions`, `JobTrack.Domain` and
`JobTrack.Application` and nothing further — never a provider assembly, never ASP.NET Core (`ReusableLibraryDependencyTests`).

Internal package — part of the JobTrack reusable library, not a standalone published product. See
the [JobTrack repository](https://github.com/lookbusy1344/JobTrack) for the full solution.
