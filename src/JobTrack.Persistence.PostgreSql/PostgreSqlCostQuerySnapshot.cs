namespace JobTrack.Persistence.PostgreSql;

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

internal static class PostgreSqlCostQuerySnapshot
{
	public static Task<IDbContextTransaction> BeginAsync(
		PostgreSqlJobTrackDbContext context, CancellationToken cancellationToken) =>
		context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
}
