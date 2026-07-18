namespace JobTrack.Identity.Tests;

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestSupport;

/// <summary>
///     <see cref="ServiceCollectionExtensions.AddJobTrackIdentitySqlite" /> registers
///     <see cref="SqliteWalPragmaInterceptor" /> alongside the connection-string-builder
///     <c>foreign_keys</c>/<c>busy_timeout</c> setup -- this asserts the interceptor actually runs on
///     every connection the resulting <see cref="JobTrackIdentityDbContext" /> opens.
/// </summary>
public sealed class SqliteWalPragmaInterceptorTests : IAsyncLifetime
{
	private readonly SqliteDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Opening_the_Identity_DbContext_connection_enables_WAL_journal_mode_and_NORMAL_synchronous()
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();

		await context.Database.OpenConnectionAsync();

		(await ReadPragmaAsync(context, "journal_mode")).Should().Be("wal");
		(await ReadPragmaAsync(context, "synchronous")).Should().Be(1L);
	}

	private static async Task<object> ReadPragmaAsync(JobTrackIdentityDbContext context, string pragmaName)
	{
		var connection = context.Database.GetDbConnection();
		await using var command = connection.CreateCommand();
		command.CommandText = $"PRAGMA {pragmaName};";
		return await command.ExecuteScalarAsync() ?? throw new InvalidOperationException($"PRAGMA {pragmaName} returned no value.");
	}
}
