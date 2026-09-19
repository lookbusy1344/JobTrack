namespace JobTrack.Persistence.PostgreSql.Tests;

using AwesomeAssertions;
using Npgsql;
using Shared;
using Shared.Ports;

/// <summary>
///     §2.2: PostgreSQL write-conflict classification by SQLSTATE. A deadlock (40P01) or serialization
///     failure (40001) is transient, not a range overlap; an exclusion violation stays a range overlap;
///     a unique violation stays a uniqueness violation.
/// </summary>
public sealed class PostgreSqlWriteConflictClassificationTests
{
	private static readonly PostgreSqlWriteOperations Operations =
		new(new NpgsqlDataSourceBuilder("Host=localhost;Database=unused").Build());

	[Theory]
	[InlineData("40P01", nameof(WriteConflictKind.Transient))] // deadlock_detected
	[InlineData("40001", nameof(WriteConflictKind.Transient))] // serialization_failure
	[InlineData("23P01", nameof(WriteConflictKind.RangeOverlap))] // exclusion_violation
	[InlineData("23505", nameof(WriteConflictKind.UniquenessViolation))] // unique_violation
	[InlineData("08006", nameof(WriteConflictKind.None))] // connection failure -- not a write conflict
	[InlineData("57014", nameof(WriteConflictKind.None))] // statement timeout -- not a write conflict
	public void Sqlstates_classify_to_the_expected_write_conflict_kind(string sqlState, string expected) =>
		Operations.ClassifyWriteConflict(Postgres(sqlState)).Should().Be(Enum.Parse<WriteConflictKind>(expected));

	[Theory]
	[InlineData("40P01", nameof(PersistenceFailure.Transient))]
	[InlineData("40001", nameof(PersistenceFailure.Transient))]
	[InlineData("40002", nameof(PersistenceFailure.Unknown))]
	[InlineData("40003", nameof(PersistenceFailure.Unknown))]
	[InlineData("23505", nameof(PersistenceFailure.Integrity))]
	[InlineData("08006", nameof(PersistenceFailure.Unknown))]
	public void Sqlstates_classify_to_the_expected_persistence_failure(string sqlState, string expected) =>
		Operations.ClassifyWriteFailure(Postgres(sqlState)).Should().Be(Enum.Parse<PersistenceFailure>(expected));

	private static PostgresException Postgres(string sqlState) => new("test", "ERROR", "ERROR", sqlState);
}
