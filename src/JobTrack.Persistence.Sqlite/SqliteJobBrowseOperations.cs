namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared;
using Shared.Ports;

/// <summary>
///     The SQLite half of <see cref="Application.Ports.IJobBrowseQueryPort" /> (plan §8.5 slice 2):
///     everything <see cref="JobBrowseQueryPort" /> cannot express provider-neutrally. Context creation
///     comes from <see cref="SqliteReadOperations" />; only the subtree-success mechanism is its own.
/// </summary>
internal sealed class SqliteJobBrowseOperations(string connectionString, IReadOnlyList<IInterceptor> interceptors)
	: SqliteReadOperations(connectionString, interceptors), IJobBrowseProviderOperations
{
	/// <summary>Creates the seam over the given SQLite connection string.</summary>
	public SqliteJobBrowseOperations(string connectionString)
		: this(connectionString, [])
	{
	}

	public async Task<bool> IsSubtreeSucceededAsync(DbContext context, long rootId, CancellationToken cancellationToken) =>
		await JobNodeHierarchyQueries.IsSubtreeAchievedSqliteAsync(
			context, rootId, (short)Achievement.Success, cancellationToken).ConfigureAwait(false);
}
