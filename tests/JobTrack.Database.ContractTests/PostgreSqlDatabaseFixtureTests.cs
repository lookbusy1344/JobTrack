namespace JobTrack.Database.ContractTests;

using AwesomeAssertions;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlDatabaseFixtureTests
{
	[Fact]
	public async Task Fixture_created_database_uses_UK_English_ICU_collation()
	{
		await using var database = new PostgreSqlDatabaseFixture();

		await database.InitializeAsync();

		await using var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT datlocprovider::text, datlocale
							  FROM pg_database
							  WHERE datname = current_database();
							  """;

		await using var reader = await command.ExecuteReaderAsync();
		await reader.ReadAsync();

		reader.GetString(0).Should().Be(PostgreSqlDatabaseFixture.IcuLocaleProviderCode);
		reader.GetString(1).Should().Be(PostgreSqlDatabaseFixture.UkEnglishIcuLocale);
	}
}
