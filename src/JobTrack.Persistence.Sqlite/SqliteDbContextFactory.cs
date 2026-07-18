namespace JobTrack.Persistence.Sqlite;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Opens every <see cref="SqliteJobTrackDbContext" /> with the project's required per-connection
///     pragmas (docs/operations/sqlite-limitations-and-configuration.md), for both read and write ports.
/// </summary>
internal static class SqliteDbContextFactory
{
	internal static SqliteJobTrackDbContext CreateContext(string connectionString) =>
		CreateContext(connectionString, []);

	/// <summary>
	///     Test-only seam (Stage 6 efficiency guards, ADR 0039): lets a test attach an interceptor --
	///     e.g. a command-count counter proving a bounded number of round trips regardless of subtree
	///     size -- without adding any production-facing constructor parameter.
	/// </summary>
	internal static SqliteJobTrackDbContext CreateContext(string connectionString, IReadOnlyList<IInterceptor> interceptors)
	{
		ArgumentException.ThrowIfNullOrEmpty(connectionString);

		var optionsBuilder = new DbContextOptionsBuilder<SqliteJobTrackDbContext>().UseSqlite(connectionString);
		if (interceptors.Count > 0) {
			optionsBuilder = optionsBuilder.AddInterceptors(interceptors);
		}

		var context = new SqliteJobTrackDbContext(optionsBuilder.Options);
		context.Database.OpenConnection();
		_ = context.Database.ExecuteSqlRaw(SqliteConnectionPragmas.ConfigureConnectionSql);

		return context;
	}

	internal static async Task<SqliteJobTrackDbContext> CreateOpenContextAsync(
		string connectionString, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(connectionString);

		var options = new DbContextOptionsBuilder<SqliteJobTrackDbContext>().UseSqlite(connectionString).Options;
		var context = new SqliteJobTrackDbContext(options);

		await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		_ = await context.Database
			.ExecuteSqlRawAsync(SqliteConnectionPragmas.ConfigureConnectionSql, cancellationToken)
			.ConfigureAwait(false);

		return context;
	}
}
