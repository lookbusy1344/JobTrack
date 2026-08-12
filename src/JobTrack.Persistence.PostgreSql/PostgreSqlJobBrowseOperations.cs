namespace JobTrack.Persistence.PostgreSql;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shared.Ports;

/// <summary>
///     The PostgreSQL half of <see cref="Application.Ports.IJobBrowseQueryPort" /> (plan §8.5 slice 2):
///     everything <see cref="JobBrowseQueryPort" /> cannot express provider-neutrally. Context creation
///     comes from <see cref="PostgreSqlReadOperations" />; only the subtree-success mechanism is its own.
/// </summary>
internal sealed class PostgreSqlJobBrowseOperations(NpgsqlDataSource dataSource, IReadOnlyList<IInterceptor> interceptors)
	: PostgreSqlReadOperations(dataSource, interceptors), IJobBrowseProviderOperations
{
	/// <summary>Creates the seam over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlJobBrowseOperations(NpgsqlDataSource dataSource)
		: this(dataSource, []) { }

	public async Task<bool> IsSubtreeSucceededAsync(DbContext context, long rootId, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<bool>(
						 $"""
						  SELECT node_succeeded({rootId}) AS "Value"
						  """)
					 .SingleAsync(cancellationToken).ConfigureAwait(false);
}
