namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Program = Program;

/// <summary>
///     ADR 0066 Stage 6: <c>Deployment:Topology=MultiInstance</c> ties the plan's per-store selectors
///     together (docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.6) -- it must fail
///     startup closed whenever any one of the process-local stores (SQLite, filesystem data
///     protection, in-process rate limiting) is still selected underneath it, rather than starting
///     successfully and silently losing cross-host correctness in production.
/// </summary>
public sealed class DeploymentTopologyConfigurationTests
{
	private const string LoopbackConnectionString = "Host=127.0.0.1;Port=5432;Database=jobtrack";
	private const string SqliteConnectionString = "Data Source=:memory:";

	[Fact]
	public void Startup_fails_closed_when_MultiInstance_topology_is_selected_with_the_Sqlite_provider()
	{
		using var factory = new ConfiguredWebApplicationFactory(
			"Sqlite", SqliteConnectionString, null, null, "MultiInstance");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment:Topology*MultiInstance*Database:Provider*PostgreSql*");
	}

	[Fact]
	public void Startup_fails_closed_when_MultiInstance_topology_is_selected_with_the_filesystem_data_protection_store()
	{
		using var factory = new ConfiguredWebApplicationFactory(
			"PostgreSql", LoopbackConnectionString, "FileSystem", "PostgreSql", "MultiInstance");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment:Topology*MultiInstance*DataProtection:Store*PostgreSql*");
	}

	[Fact]
	public void Startup_fails_closed_when_MultiInstance_topology_is_selected_with_no_data_protection_store_configured()
	{
		using var factory = new ConfiguredWebApplicationFactory(
			"PostgreSql", LoopbackConnectionString, null, "PostgreSql", "MultiInstance");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment:Topology*MultiInstance*DataProtection:Store*PostgreSql*");
	}

	[Fact]
	public void Startup_fails_closed_when_MultiInstance_topology_is_selected_with_the_in_process_rate_limiting_store()
	{
		using var factory = new ConfiguredWebApplicationFactory(
			"PostgreSql", LoopbackConnectionString, "PostgreSql", "InProcess", "MultiInstance");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment:Topology*MultiInstance*RateLimiting:Store*PostgreSql*");
	}

	[Fact]
	public void Startup_fails_closed_when_MultiInstance_topology_is_selected_with_no_rate_limiting_store_configured()
	{
		using var factory = new ConfiguredWebApplicationFactory(
			"PostgreSql", LoopbackConnectionString, "PostgreSql", null, "MultiInstance");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment:Topology*MultiInstance*RateLimiting:Store*PostgreSql*");
	}

	[Fact]
	public void Startup_fails_closed_when_Deployment_Topology_names_neither_supported_value()
	{
		using var factory = new ConfiguredWebApplicationFactory(
			"PostgreSql", LoopbackConnectionString, "PostgreSql", "PostgreSql", "Federated");

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment:Topology*SingleInstance*MultiInstance*");
	}

	private sealed class ConfiguredWebApplicationFactory(
		string databaseProvider,
		string identityConnectionString,
		string? dataProtectionStore,
		string? rateLimitingStore,
		string deploymentTopology)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", databaseProvider);
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("Deployment:Topology", deploymentTopology);
			if (dataProtectionStore is not null) {
				_ = builder.UseSetting("DataProtection:Store", dataProtectionStore);
			}

			if (rateLimitingStore is not null) {
				_ = builder.UseSetting("RateLimiting:Store", rateLimitingStore);
			}
		}
	}
}
