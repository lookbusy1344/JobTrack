namespace JobTrack.TestSupport;

using Npgsql;

/// <summary>
///     Creates a uniquely named, disposable PostgreSQL database on the local
///     instance for one test class, and drops it on disposal (§6.6: shared
///     database contract suite runs against disposable real PostgreSQL
///     databases). Connect as an administrator via
///     <c>JOBTRACK_TEST_POSTGRES_ADMIN_CONNECTION_STRING</c>, defaulting to the
///     local instance's Unix-socket, peer-authenticated connection (pg_hba.conf
///     requires a password over TCP but trusts the OS user over the socket).
/// </summary>
public sealed class PostgreSqlDatabaseFixture : IDisposableTestDatabase
{
	public const string IcuLocaleProviderCode = "i";
	public const string UkEnglishIcuLocale = "en-GB";

	private const string AdminConnectionStringEnvironmentVariable = "JOBTRACK_TEST_POSTGRES_ADMIN_CONNECTION_STRING";
	private const string DefaultUnixSocketDirectory = "/tmp";

	private readonly string databaseName = $"jobtrack_test_{Guid.NewGuid():N}";

	public string ConnectionString { get; private set; } = string.Empty;

	public async Task InitializeAsync()
	{
		await using var adminConnection = new NpgsqlConnection(AdminConnectionString());
		await adminConnection.OpenAsync().ConfigureAwait(false);

		await using (var command = adminConnection.CreateCommand()) {
			// databaseName is a fixture-generated GUID, never external input;
			// PostgreSQL cannot parameterize a CREATE DATABASE identifier.
			command.CommandText = $"""
								   CREATE DATABASE "{databaseName}"
								       LOCALE_PROVIDER icu
								       ICU_LOCALE '{UkEnglishIcuLocale}'
								       TEMPLATE template0;
								   """;
			_ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
		}

		var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString()) { Database = databaseName };

		ConnectionString = builder.ConnectionString;
	}

	public async Task DisposeAsync()
	{
		await using var adminConnection = new NpgsqlConnection(AdminConnectionString());
		await adminConnection.OpenAsync().ConfigureAwait(false);

		await using var command = adminConnection.CreateCommand();
		command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);";
		_ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
	}

	private static string AdminConnectionString() =>
		Environment.GetEnvironmentVariable(AdminConnectionStringEnvironmentVariable)
		?? new NpgsqlConnectionStringBuilder { Host = DefaultUnixSocketDirectory, Username = Environment.UserName, Database = "postgres" }
			.ConnectionString;
}
