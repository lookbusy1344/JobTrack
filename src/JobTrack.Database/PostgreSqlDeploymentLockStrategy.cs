namespace JobTrack.Database;

using System.Data;
using System.Data.Common;
using Npgsql;

/// <summary>
///     Serializes concurrent deployment-tool runs using the ADR 0012 "schema
///     deployment" advisory-lock domain: a transaction-scoped lock, reacquired
///     inside each applied script's own transaction, so the lock is always
///     released on commit or rollback and never survives a connection handoff.
/// </summary>
public sealed class PostgreSqlDeploymentLockStrategy : IDeploymentLockStrategy
{
	public IsolationLevel TransactionIsolationLevel => IsolationLevel.ReadCommitted;

	public async Task AcquireAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
	{
		var npgsqlConnection = (NpgsqlConnection)connection;

		await using var command = npgsqlConnection.CreateCommand();
		command.Transaction = (NpgsqlTransaction)transaction;
		command.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@lockDomain));";
		_ = command.Parameters.AddWithValue("lockDomain", PostgreSqlLockKeys.SchemaDeployment);

		_ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}
}
