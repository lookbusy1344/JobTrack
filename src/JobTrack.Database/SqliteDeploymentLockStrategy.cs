namespace JobTrack.Database;

using System.Data;
using System.Data.Common;

/// <summary>
///     SQLite has no advisory-lock primitive; instead each script transaction is
///     started with <c>BEGIN IMMEDIATE</c> (Serializable, non-deferred), which
///     takes SQLite's write lock immediately and serializes concurrent
///     deployment-tool runs through SQLite's single-writer model (§6.4). No
///     additional explicit lock acquisition is needed.
/// </summary>
public sealed class SqliteDeploymentLockStrategy : IDeploymentLockStrategy
{
	public IsolationLevel TransactionIsolationLevel => IsolationLevel.Serializable;

	public Task AcquireAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
		Task.CompletedTask;
}
