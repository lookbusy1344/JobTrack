namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using AwesomeAssertions;
using NpgsqlTypes;

/// <summary>
///     Shared read-back/drift assertion helper for the generated <c>daterange</c>/<c>tstzrange</c>
///     columns added by the PostgreSQL column-type remediation plan
///     (docs/plans/2026-07-11-postgresql-column-type-remediation-plan.md §3.1): proves each generated
///     range column matches the scalar start/end columns it was derived from, for one finite and one
///     unbounded row per table. Used only from PostgreSQL-specific contract test subclasses -- SQLite
///     has no equivalent generated column.
/// </summary>
internal static class PostgreSqlGeneratedRangeAssertions
{
	public static async Task<NpgsqlRange<DateTime>> ReadTstzRangeAsync(DbConnection connection, string table, string column, long id) =>
		await ReadRangeAsync<NpgsqlRange<DateTime>>(connection, table, column, id);

	public static async Task<NpgsqlRange<DateOnly>> ReadDateRangeAsync(DbConnection connection, string table, string column, long id) =>
		await ReadRangeAsync<NpgsqlRange<DateOnly>>(connection, table, column, id);

	// The generated columns use COALESCE(effective_end, 'infinity'::timestamptz | 'infinity'::date)
	// (schema versions 0009/0010/0011), not a syntactically unbounded range -- so an open-ended row
	// reads back as a finite range whose upper bound is the infinity sentinel value, which Npgsql
	// maps to DateTime.MaxValue/DateOnly.MaxValue, rather than one with UpperBoundInfinite set.

	public static void AssertMatches(NpgsqlRange<DateTime> range, DateTimeOffset lowerBound, DateTimeOffset? upperBound)
	{
		range.LowerBound.Should().Be(lowerBound.UtcDateTime);
		range.LowerBoundIsInclusive.Should().BeTrue();
		range.UpperBound.Should().Be(upperBound?.UtcDateTime ?? DateTime.MaxValue);
		range.UpperBoundIsInclusive.Should().BeFalse();
	}

	public static void AssertMatches(NpgsqlRange<DateOnly> range, DateOnly lowerBound, DateOnly? upperBound)
	{
		range.LowerBound.Should().Be(lowerBound);
		range.LowerBoundIsInclusive.Should().BeTrue();
		range.UpperBound.Should().Be(upperBound ?? DateOnly.MaxValue);
		range.UpperBoundIsInclusive.Should().BeFalse();
	}

	private static async Task<T> ReadRangeAsync<T>(DbConnection connection, string table, string column, long id)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT {column} FROM {table} WHERE id = @id;";
		var parameter = command.CreateParameter();
		parameter.ParameterName = "@id";
		parameter.Value = id;
		command.Parameters.Add(parameter);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		return reader.GetFieldValue<T>(0);
	}
}
