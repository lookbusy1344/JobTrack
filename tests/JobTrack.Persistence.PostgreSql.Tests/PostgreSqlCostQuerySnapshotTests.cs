namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlCostQuerySnapshotTests : IAsyncLifetime
{
	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Cost_query_snapshot_uses_repeatable_read()
	{
		await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, provider => provider.UseNodaTime()).Options;
		await using var context = new PostgreSqlJobTrackDbContext(options);

		await using var transaction = await PostgreSqlCostQuerySnapshot.BeginAsync(context, CancellationToken.None);

		transaction.GetDbTransaction().IsolationLevel.Should().Be(IsolationLevel.RepeatableRead);
	}
}
