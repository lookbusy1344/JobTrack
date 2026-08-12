namespace JobTrack.Persistence.PostgreSql;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shared.Ports;

/// <summary>
///     The PostgreSQL half of <see cref="Application.Ports.IWorkSessionQueryPort" /> (plan §8.5 slice
///     4): everything <see cref="WorkSessionQueryPort" /> cannot express provider-neutrally.
/// </summary>
internal sealed class PostgreSqlWorkSessionQueryOperations(NpgsqlDataSource dataSource, IReadOnlyList<IInterceptor> interceptors)
	: PostgreSqlReadOperations(dataSource, interceptors), IWorkSessionQueryProviderOperations
{
	/// <summary>Creates the seam over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlWorkSessionQueryOperations(NpgsqlDataSource dataSource)
		: this(dataSource, []) { }

	public async Task<IReadOnlyList<long>> GetControlledLeafIdsAsync(
		DbContext context, long actorId, IReadOnlyList<long> leafWorkIds, CancellationToken cancellationToken)
	{
		var leafWorkIdValues = leafWorkIds.ToArray();
		return await context.Database.SqlQuery<long>(
			$"""
			 SELECT controlled_leaf_id AS "Value"
			 FROM job_node_controlled_leaf_ids({actorId}, {leafWorkIdValues})
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);
	}
}
