namespace JobTrack.Web.IntegrationTests;

using System.Net;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestSupport;
using Program = Program;

/// <summary>
///     ADR 0066 Stage 6: <c>/health/live</c> (process-only liveness) and <c>/health/ready</c>
///     (dependency-checked readiness) (plan §2.6). Both are anonymous and outside the
///     <c>/api</c>-prefixed rate-limiting middleware, so no explicit exemption wiring is needed --
///     these tests prove that structural exemption holds rather than merely asserting it by reading
///     the routing code.
/// </summary>
public sealed class HealthEndpointTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		factory = new(database.ConnectionString);
		client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
	}

	public async Task DisposeAsync()
	{
		Dispose();
		await database.DisposeAsync();
	}

	public void Dispose()
	{
		client.Dispose();
		factory.Dispose();
	}

	[Fact]
	public async Task Health_live_returns_200_with_no_authentication_and_no_body_leak()
	{
		using var response = await client.GetAsync("/health/live");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().NotContain("Exception").And.NotContain("System.");
	}

	[Fact]
	public async Task Health_live_never_redirects_to_the_login_page()
	{
		using var response = await client.GetAsync("/health/live");

		response.StatusCode.Should().NotBe(HttpStatusCode.Found);
		response.Headers.Location.Should().BeNull();
	}

	[Fact]
	public async Task Health_ready_returns_200_when_the_domain_database_is_reachable()
	{
		using var response = await client.GetAsync("/health/ready");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Health_ready_returns_503_without_leaking_exception_or_version_detail_when_the_database_is_unreachable()
	{
		var unreachablePath = Path.Combine(Path.GetTempPath(), $"jobtrack_missing_{Guid.NewGuid():N}", "unreachable.db");
		var unreachableConnectionString = new SqliteConnectionStringBuilder { DataSource = unreachablePath }.ConnectionString;
		using var unreachableFactory = new TestWebApplicationFactory(unreachableConnectionString);
		using var unreachableClient = unreachableFactory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

		using var response = await unreachableClient.GetAsync("/health/ready");

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().NotContain("Exception").And.NotContain("System.").And.NotContain(ApplicationVersion);
	}

	[Fact]
	public async Task Health_ready_honours_a_cancelled_request()
	{
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var act = () => client.GetAsync("/health/ready", cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task Readiness_stops_accepting_traffic_as_soon_as_shutdown_begins()
	{
		var readinessState = factory.Services.GetRequiredService<ApplicationReadinessState>();
		var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
		readinessState.IsAcceptingTraffic.Should().BeTrue();

		lifetime.StopApplication();

		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (readinessState.IsAcceptingTraffic && DateTime.UtcNow < deadline) {
			await Task.Delay(10);
		}

		readinessState.IsAcceptingTraffic.Should().BeFalse("ApplicationStopping must flip readiness before the process actually exits");
	}

	[Fact]
	public async Task Repeated_health_live_requests_never_return_429()
	{
		for (var i = 0; i < 5; ++i) {
			using var response = await client.GetAsync("/health/live");
			response.StatusCode.Should().NotBe((HttpStatusCode)429);
		}
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

	private sealed class TestWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}
}
