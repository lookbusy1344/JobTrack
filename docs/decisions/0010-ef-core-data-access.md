# ADR 0010: EF Core as the single data-access technology, EF-first with stored-function encapsulation

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 7

## Decision

EF Core 10 is the single, strongly preferred data-access technology for both providers: `Npgsql.EntityFrameworkCore.PostgreSQL` for PostgreSQL, `Microsoft.EntityFrameworkCore.Sqlite` for SQLite. Every read and write is authored in LINQ/EF first — ordinary persistence mapping, command change tracking, LINQ read models, application-managed `bigint` concurrency properties (see ADR 0006 (`0006-identifiers.md`)), and value conversion (including ADR 0007 (`0007-sqlite-instant-encoding.md`)'s instant converter).

Hand-authored SQL is a last resort, permitted only where EF genuinely cannot express or plan the operation correctly:

- recursive-CTE hierarchy and prerequisite-graph queries;
- PostgreSQL range/exclusion operations (GiST);
- deferred-constraint workflows;
- advisory locks (see ADR 0012 (`0012-postgresql-lock-keys.md`));
- database-wide overlap discovery; and
- the canonical cost-input queries (§6.5).

Where irreducible SQL is required **on PostgreSQL**, it is encapsulated as a source-controlled stored function or procedure (UDF, table-valued function, or `PROCEDURE`), deployed by the versioned migration scripts (see ADR 0011 (`0011-schema-deployment-versioning.md`)), and invoked **through** EF:

- composable functions are mapped into LINQ with `modelBuilder.HasDbFunction(...)`, so they participate in query composition and plan review;
- non-composable logic is invoked with `FromSql`/`SqlQuery`/`ExecuteSql`.

Inline raw-SQL string literals scattered beside call sites are explicitly not the pattern — the SQL is a named, versioned database object with a stable logical identifier used for error translation (constraint/SQLSTATE mapping), reviewable independently of the C# call site.

SQLite has no stored procedures. Its irreducible logic (overlap/graph guards, single-root and leaf/branch exclusivity) lives in the enforcement triggers already planned for schema enforcement (§6.4), plus minimal parameterized statements where EF cannot help; application-side computation is preferred over inline SQL wherever it can substitute.

EF Core *migrations* are explicitly **not** the production schema mechanism (unchanged from the plan) — the authoritative schema uses PostgreSQL features (deferred constraint triggers, GiST exclusion, roles/grants) EF's migration model cannot represent, and production deployment requires forward-only, checksum-verified, explicitly reviewed SQL (see ADR 0011 (`0011-schema-deployment-versioning.md`)).

## Why EF over a thinner mapper (e.g. Dapper)

EF earns its place through change tracking for commands, the Identity store integration, application-managed optimistic-concurrency tokens as first-class concurrency properties, and value conversion for domain value objects — all of which a thin mapper would require hand-rolling per entity. The encapsulate-as-function rule is what keeps the residual hand-authored SQL reviewable rather than sprawling through the persistence assembly; it is the deliberate mitigation for EF's known weakness on recursive/graph/exclusion queries, not a reason to avoid EF for the majority of straightforward mapping.

## Consequences

- The EF model configuration shared by both providers (entity mappings, value converters, concurrency-token setup, `HasDbFunction` registrations) lives in one internal shared configuration component referenced by both persistence assemblies (§7.4), so the providers cannot drift from each other or from the reviewed SQL schema independently.
- Model-to-schema validation tests assert EF mappings match the deployed SQL schema exactly — a drifted mapping fails a test, not a production query.
- Every `HasDbFunction`/`FromSql` call site has a direct integration test against both providers' equivalent behaviour (identical public result or stable error category).
- `EnsureCreated` and automatic startup migrations are never used; the explicit schema deployment tool (§6.1, ADR 0011 (`0011-schema-deployment-versioning.md`)) is the only path to a production schema.
