namespace JobTrack.Persistence.Sqlite;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared.Ports;

/// <summary>
///     The SQLite read-path seam (ADR 0064), shared by every query port whose body lives in
///     <c>JobTrack.Persistence.Shared</c>. One <see cref="SqliteJobTrackDbContext" /> per call, opened
///     with the required per-connection pragmas
///     (docs/operations/sqlite-limitations-and-configuration.md).
/// </summary>
internal class SqliteReadOperations(string connectionString, IReadOnlyList<IInterceptor> interceptors) : IProviderReadOperations
{
	/// <summary>Creates the seam over the given SQLite connection string.</summary>
	public SqliteReadOperations(string connectionString)
		: this(connectionString, []) { }

	public DbContext CreateContext() => SqliteDbContextFactory.CreateContext(connectionString, interceptors);
}
