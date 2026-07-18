namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using TestSupport;
using Program = Program;

/// <summary>
///     Plan §8.2 / fix-plan §2.4: outside Development, missing forwarded-header trust configuration
///     or a missing data-protection key path must fail startup closed rather than silently trusting an
///     unconfigured reverse-proxy boundary or falling back to an ephemeral key ring.
/// </summary>
public sealed class ProductionSecurityConfigurationTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly SqliteDatabaseFixture database = new();

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();
	}

	public async Task DisposeAsync() => await database.DisposeAsync();

	public void Dispose()
	{
	}

	[Fact]
	public void Startup_fails_closed_outside_development_without_forwarded_header_configuration()
	{
		using var factory = new UnconfiguredProductionWebApplicationFactory(database.ConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*ForwardedHeaders*trusted reverse proxy outside Development*");
	}

	[Fact]
	public void Startup_fails_closed_outside_development_without_a_data_protection_key_path()
	{
		using var factory = new UnconfiguredDataProtectionWebApplicationFactory(database.ConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:KeyPath*outside Development*");
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private sealed class UnconfiguredProductionWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}

	// Configures a trusted forwarded-header proxy (so that earlier fail-closed check passes) but
	// leaves DataProtection:KeyPath unset, isolating the data-protection half of the same
	// fail-closed startup sequence from the forwarded-headers half proven above.
	private sealed class UnconfiguredDataProtectionWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("ForwardedHeaders:KnownProxies:0", "127.0.0.1");
		}
	}
}
