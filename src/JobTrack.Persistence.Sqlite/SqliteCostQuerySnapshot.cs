namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

internal static class SqliteCostQuerySnapshot
{
	public static async Task<IDbContextTransaction> BeginAsync(
		SqliteJobTrackDbContext context, CancellationToken cancellationToken)
	{
		await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		var connection = (SqliteConnection)context.Database.GetDbConnection();
		var transaction = connection.BeginTransaction(IsolationLevel.Serializable, true);
		return await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false)
			   ?? throw new InvalidOperationException("SQLite did not attach the cost-query snapshot transaction.");
	}
}
