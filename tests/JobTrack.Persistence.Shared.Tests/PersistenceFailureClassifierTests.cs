namespace JobTrack.Persistence.Shared.Tests;

using System.Data.Common;
using AwesomeAssertions;

/// <summary>
///     Unit tests for the §2.2 failure classifier over the provider-agnostic <see cref="DbException" />
///     surface: PostgreSQL SQLSTATE classes on <see cref="DbException.SqlState" />. SQLite's result
///     codes are provider-specific and are intentionally covered in its provider test assembly rather
///     than guessed from <see cref="DbException.IsTransient" />.
/// </summary>
public sealed class PersistenceFailureClassifierTests
{
	[Theory]
	[InlineData("40001", nameof(PersistenceFailure.Transient))] // serialization_failure
	[InlineData("40P01", nameof(PersistenceFailure.Transient))] // deadlock_detected
	[InlineData("40002", nameof(PersistenceFailure.Unknown))] // transaction_integrity_constraint_violation
	[InlineData("40003", nameof(PersistenceFailure.Unknown))] // statement_completion_unknown
	[InlineData("23505", nameof(PersistenceFailure.Integrity))] // unique_violation
	[InlineData("23503", nameof(PersistenceFailure.Integrity))] // foreign_key_violation
	[InlineData("P0007", nameof(PersistenceFailure.Integrity))] // project leaf-closed trigger
	[InlineData("08006", nameof(PersistenceFailure.Unknown))] // connection failure
	[InlineData("57014", nameof(PersistenceFailure.Unknown))] // statement timeout
	[InlineData("53200", nameof(PersistenceFailure.Unknown))] // out of memory
	public void PostgreSql_sqlstate_classes_map_to_the_expected_failure(string sqlState, string expected) =>
		PersistenceFailureClassifier.Classify(new FakeDbException(sqlState, false)).Should().Be(Enum.Parse<PersistenceFailure>(expected));


	[Fact]
	public void A_database_failure_without_a_sqlstate_is_unknown()
	{
		var providerSpecificFailure = new FakeDbException(null, false);

		PersistenceFailureClassifier.Classify(providerSpecificFailure).Should().Be(PersistenceFailure.Unknown);
	}

	[Fact]
	public void The_classifier_walks_the_exception_chain_to_the_first_db_exception()
	{
		var wrapped = new InvalidOperationException("EF wrapper", new FakeDbException("40P01", false));

		PersistenceFailureClassifier.Classify(wrapped).Should().Be(PersistenceFailure.Transient);
	}

	[Fact]
	public void A_non_database_exception_is_unknown() =>
		PersistenceFailureClassifier.Classify(new InvalidOperationException("not a database failure")).Should().Be(PersistenceFailure.Unknown);

	private sealed class FakeDbException(string? sqlState, bool isTransient) : DbException
	{
		public override string? SqlState => sqlState;

		public override bool IsTransient => isTransient;
	}
}
