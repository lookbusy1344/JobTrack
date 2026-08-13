namespace JobTrack.Persistence.PostgreSql.Tests;

using Npgsql;

internal static class PostgreSqlRoleDataSource
{
	public static NpgsqlDataSourceBuilder CreateBuilder(string connectionString, string role)
	{
		var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString) {
			Options = $"-c role={role}",
		};
		return new NpgsqlDataSourceBuilder(connectionStringBuilder.ConnectionString).UseNodaTime();
	}
}
