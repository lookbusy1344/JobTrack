# JobTrack.Persistence.Shared

Internal EF Core model configuration (entity mappings, strongly typed identifier converters,
concurrency-token setup) shared by `JobTrack.Persistence.PostgreSql` and
`JobTrack.Persistence.Sqlite`, so the two providers cannot drift independently of each other or
of the reviewed SQL schema (impl plan §7.4, ADR 0010). Every type is internal; nothing here is
part of the public library surface.

Internal package — part of the JobTrack reusable library, not a standalone published product.
See the [JobTrack repository](https://github.com/lookbusy1344/VSStuff) for the full solution.
