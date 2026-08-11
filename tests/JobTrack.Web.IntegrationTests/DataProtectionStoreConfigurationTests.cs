namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Program = Program;

/// <summary>
///     ADR 0066 Stage 2: <c>DataProtection:Store=PostgreSql</c> is the multi-instance key repository
///     (docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.2) and is valid only alongside
///     <c>Database:Provider=PostgreSql</c> -- there is no SQLite equivalent (the plan's provider
///     boundary), so the combination must fail startup closed rather than silently falling back to a
///     filesystem key ring or throwing some unrelated, harder-to-diagnose error deeper in the pipeline.
/// </summary>
public sealed class DataProtectionStoreConfigurationTests
{
	private const string LoopbackConnectionString = "Host=127.0.0.1;Port=5432;Database=jobtrack";
	private const string SqliteConnectionString = "Data Source=:memory:";

	[Fact]
	public void Startup_fails_closed_when_the_PostgreSql_data_protection_store_is_selected_with_the_Sqlite_provider()
	{
		using var factory = new ConfiguredWebApplicationFactory("Sqlite", SqliteConnectionString, "PostgreSql");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:Store*PostgreSql*Database:Provider*PostgreSql*");
	}

	[Fact]
	public void Startup_fails_closed_when_DataProtection_Store_names_neither_supported_value()
	{
		using var factory = new ConfiguredWebApplicationFactory("PostgreSql", LoopbackConnectionString, "Redis");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:Store*FileSystem*PostgreSql*");
	}

	private sealed class ConfiguredWebApplicationFactory(string databaseProvider, string identityConnectionString, string dataProtectionStore)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", databaseProvider);
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("DataProtection:Store", dataProtectionStore);
		}
	}
}
