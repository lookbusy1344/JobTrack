namespace JobTrack.Persistence.Sqlite.Tests;

using System.Data;
using Abstractions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Ports;
using TestSupport;

/// <summary>
///     §2.2: SQLite write-conflict classification. A busy/locked database is transient, a foreign-key
///     failure is not a range overlap, and a unique-index rejection stays a uniqueness violation.
/// </summary>
public sealed class SqliteWriteConflictClassificationTests
{
	private const int ConstraintErrorCode = 19;
	private const int BusyErrorCode = 5;
	private const int LockedErrorCode = 6;
	private const int ForeignKeyExtendedErrorCode = 787;
	private const int UniqueExtendedErrorCode = 2067;
	private const int IoErrorCode = 10;
	private const int BusyTimeoutMilliseconds = 1;
	private const int CommandTimeoutSeconds = 1;

	private static readonly SqliteWriteOperations Operations = new("Data Source=:memory:");

	[Fact]
	public void A_busy_database_is_transient() =>
		Operations.ClassifyWriteConflict(new SqliteException("database is locked", BusyErrorCode, BusyErrorCode))
				  .Should().Be(WriteConflictKind.Transient);

	[Fact]
	public void A_locked_table_is_transient() =>
		Operations.ClassifyWriteConflict(new SqliteException("database table is locked", LockedErrorCode, LockedErrorCode))
				  .Should().Be(WriteConflictKind.Transient);

	[Fact]
	public void A_busy_database_is_a_transient_persistence_failure() =>
		Operations.ClassifyWriteFailure(new SqliteException("database is locked", BusyErrorCode, BusyErrorCode))
				  .Should().Be(PersistenceFailure.Transient);

	[Fact]
	public void A_constraint_failure_is_an_integrity_persistence_failure() =>
		Operations.ClassifyWriteFailure(new SqliteException("UNIQUE constraint failed", ConstraintErrorCode, UniqueExtendedErrorCode))
				  .Should().Be(PersistenceFailure.Integrity);

	[Fact]
	public void An_unknown_sqlite_failure_is_not_an_integrity_persistence_failure() =>
		Operations.ClassifyWriteFailure(new SqliteException("disk I/O error", IoErrorCode, IoErrorCode))
				  .Should().Be(PersistenceFailure.Unknown);

	[Fact]
	public async Task A_locked_database_while_beginning_a_write_transaction_is_transient()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();
		try {
			var connectionString = new SqliteConnectionStringBuilder(database.ConnectionString) {
				Cache = SqliteCacheMode.Shared,
				DefaultTimeout = CommandTimeoutSeconds,
			}.ToString();
			var operations = new SqliteWriteOperations(connectionString);
			await using var context = await operations.CreateOpenContextAsync(CancellationToken.None);
			context.Database.SetCommandTimeout(CommandTimeoutSeconds);
			await using (var command = context.Database.GetDbConnection().CreateCommand()) {
				command.CommandText = FormattableString.Invariant($"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");
				_ = await command.ExecuteNonQueryAsync();
			}
			await using var blocker = new SqliteConnection(connectionString);
			await blocker.OpenAsync();
			await using (var command = blocker.CreateCommand()) {
				command.CommandText = SqliteConnectionPragmas.ConfigureConnectionSql;
				_ = await command.ExecuteNonQueryAsync();
			}

			await using var blockingTransaction = blocker.BeginTransaction(IsolationLevel.Serializable, false);

			Func<Task> act = async () =>
				_ = await operations.BeginWriteTransactionAsync(context, CancellationToken.None);

			await act.Should().ThrowExactlyAsync<TransientPersistenceException>();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public void A_foreign_key_failure_is_not_a_range_overlap() =>
		Operations.ClassifyWriteConflict(new SqliteException("FOREIGN KEY constraint failed", ConstraintErrorCode, ForeignKeyExtendedErrorCode))
				  .Should().NotBe(WriteConflictKind.RangeOverlap);

	[Fact]
	public void A_unique_index_rejection_is_a_uniqueness_violation() =>
		Operations.ClassifyWriteConflict(new SqliteException("UNIQUE constraint failed", ConstraintErrorCode, UniqueExtendedErrorCode))
				  .Should().Be(WriteConflictKind.UniquenessViolation);
}
