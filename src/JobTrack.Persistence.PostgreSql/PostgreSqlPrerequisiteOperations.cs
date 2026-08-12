namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shared.Ports;

/// <summary>
///     The PostgreSQL half of <see cref="Application.Ports.IPrerequisiteQueryPort" />: everything
///     <see cref="PrerequisiteQueryPort" /> cannot express provider-neutrally.
/// </summary>
internal sealed class PostgreSqlPrerequisiteOperations(NpgsqlDataSource dataSource)
	: PostgreSqlReadOperations(dataSource), IPrerequisiteProviderOperations
{
	public async Task<bool> HasActiveDependentWorkAsync(
		DbContext context, JobNodeId requiredJobId, CancellationToken cancellationToken) =>
		await PrerequisiteReadinessSerialization.HasActiveDependentWorkAsync(context, requiredJobId, cancellationToken)
												.ConfigureAwait(false);
}
