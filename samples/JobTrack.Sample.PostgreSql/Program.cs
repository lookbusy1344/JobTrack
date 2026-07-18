using JobTrack.Persistence.PostgreSql;
using Npgsql;

internal static class Program
{
	private const string SmokeConnectionString = "Host=/tmp;Database=jobtrack-sample-smoke";

	public static async Task<int> Main()
	{
		await using var dataSource = NpgsqlDataSource.Create(SmokeConnectionString);
		var client = JobTrackPostgreSql.Create(dataSource);

		return client.Jobs is not null && client.Query is not null ? 0 : 1;
	}
}
