namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using Shared.Ports;

/// <summary>
///     The SQLite half of <see cref="Application.Ports.IPrerequisiteQueryPort" />: everything
///     <see cref="PrerequisiteQueryPort" /> cannot express provider-neutrally.
/// </summary>
internal sealed class SqlitePrerequisiteOperations(string connectionString)
	: SqliteReadOperations(connectionString), IPrerequisiteProviderOperations
{
	public async Task<bool> HasActiveDependentWorkAsync(
		DbContext context, JobNodeId requiredJobId, CancellationToken cancellationToken) =>
		await PrerequisiteReadinessSerialization.HasActiveDependentWorkAsync(context, requiredJobId, cancellationToken)
												.ConfigureAwait(false);
}
