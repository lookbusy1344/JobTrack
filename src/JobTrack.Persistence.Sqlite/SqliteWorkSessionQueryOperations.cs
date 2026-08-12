namespace JobTrack.Persistence.Sqlite;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared.Ports;

/// <summary>
///     The SQLite half of <see cref="Application.Ports.IWorkSessionQueryPort" /> (plan §8.5 slice 4):
///     everything <see cref="WorkSessionQueryPort" /> cannot express provider-neutrally.
/// </summary>
internal sealed class SqliteWorkSessionQueryOperations(string connectionString, IReadOnlyList<IInterceptor> interceptors)
	: SqliteReadOperations(connectionString, interceptors), IWorkSessionQueryProviderOperations
{
	/// <summary>Creates the seam over the given SQLite connection string.</summary>
	public SqliteWorkSessionQueryOperations(string connectionString)
		: this(connectionString, []) { }

	public async Task<IReadOnlyList<long>> GetControlledLeafIdsAsync(
		DbContext context, long actorId, IReadOnlyList<long> leafWorkIds, CancellationToken cancellationToken) =>
		await SqliteControlledLeafQuery.GetControlledLeafIdsAsync(context, actorId, leafWorkIds, cancellationToken)
									   .ConfigureAwait(false);
}
