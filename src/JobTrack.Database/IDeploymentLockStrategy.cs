namespace JobTrack.Database;

using System.Data;
using System.Data.Common;

/// <summary>
///     Provider-specific mechanism serializing concurrent deployment-tool runs
///     against the same database (impl plan §6.1; ADR 0012's "schema deployment"
///     lock domain on PostgreSQL, SQLite's single-writer model elsewhere).
/// </summary>
public interface IDeploymentLockStrategy
{
	IsolationLevel TransactionIsolationLevel { get; }

	Task AcquireAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}
