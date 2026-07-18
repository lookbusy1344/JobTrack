namespace JobTrack.Database.ContractTests;

using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     Proves <see cref="Program.DeployAsync" /> resolves the roles-and-grants
///     script relative to the effective scripts root rather than the
///     executable's <see cref="AppContext.BaseDirectory" /> — the latter is
///     never populated with a copy of <c>database/</c>, so an explicit
///     <c>--scripts-root</c> (the README's documented invocation) must still
///     find the sibling <c>roles/</c> directory.
/// </summary>
public sealed class PostgreSqlDeployCommandTests : IAsyncLifetime
{
	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploy_applies_roles_and_grants_when_scripts_root_is_explicit_and_not_next_to_the_executable()
	{
		var options = new DeployCommandOptions {
			Provider = SchemaProvider.PostgreSql,
			ConnectionString = database.ConnectionString,
			ScriptsRoot = RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql),
		};

		await Program.DeployAsync(options, CancellationToken.None);

		await using var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT 1 FROM pg_roles WHERE rolname = 'jobtrack_application';";
		var result = await command.ExecuteScalarAsync();

		result.Should().Be(1);
	}
}
