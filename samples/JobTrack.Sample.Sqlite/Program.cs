using JobTrack.Persistence.Sqlite;

internal static class Program
{
	private const string SmokeConnectionString = "Data Source=jobtrack-sample-smoke.db";

	public static int Main()
	{
		var client = JobTrackSqlite.Create(SmokeConnectionString);

		return client.Jobs is not null && client.Query is not null ? 0 : 1;
	}
}
