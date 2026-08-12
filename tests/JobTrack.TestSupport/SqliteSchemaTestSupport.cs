namespace JobTrack.TestSupport;

using Database;
using Microsoft.Data.Sqlite;

public static class SqliteSchemaTestSupport
{
	public static async Task DeployAsync(string connectionString, string applicationVersion, string appliedBy)
	{
		await using var connection = new SqliteConnection(connectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(
			connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), applicationVersion, appliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
