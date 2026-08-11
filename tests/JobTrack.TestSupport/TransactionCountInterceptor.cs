namespace JobTrack.TestSupport;

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Records transactions started by an EF Core query port so contract tests can prove that a
///     multi-statement read is pinned to one database snapshot.
/// </summary>
public sealed class TransactionCountInterceptor : DbTransactionInterceptor
{
	private readonly List<IsolationLevel> isolationLevels = [];

	public int Count => isolationLevels.Count;

	public IReadOnlyList<IsolationLevel> IsolationLevels => isolationLevels;

	public override ValueTask<DbTransaction> TransactionStartedAsync(
		DbConnection connection,
		TransactionEndEventData eventData,
		DbTransaction result,
		CancellationToken cancellationToken = default)
	{
		isolationLevels.Add(result.IsolationLevel);
		return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
	}
}
