namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     JSON-parsed OpenAPI contract tests for the external HTTP API (remediation plan §3.2, ADR 0030):
///     the accepted route set is asserted exactly (present and, by construction of an exact match,
///     anything out of scope), every operation's documented problem-response status codes are asserted
///     against a maintained table, bearer authentication (ADR 0029) is asserted present on every
///     operation, and the document is scanned for security-sensitive leakage. These supersede the
///     substring-only checks this suite carried while each vertical slice was still landing.
/// </summary>
public sealed class OpenApiContractTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	// Every mutation with a JSON request body documents 413 (request-body-size limit, plan §4.4);
	// every operation documents 429 (per-caller rate limiting, plan §4.4) via the route-group-level
	// annotation. 403 is documented on every operation via the route-group filter for forced password
	// change (§2.1); 404/409/400 remain per-operation because they are not universal.
	private static readonly OperationContract[] ExpectedContract = [
		new("GET", "/api/antiforgery-token", ["401", "403", "429"]),
		new("GET", "/api/employees/{userId}/rates", ["400", "401", "403", "404", "429"]),
		new("POST", "/api/employees/{userId}/rates/user-cost-rates", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/employees/{userId}/rates/user-cost-rates/{rateId}/correct", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/employees/{userId}/rates/node-rate-overrides", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/employees/{userId}/rates/node-rate-overrides/{overrideId}/correct", ["400", "401", "403", "404", "409", "413", "429"]),
		new("GET", "/api/jobs/root", ["401", "403", "429"]),
		new("GET", "/api/jobs/search", ["400", "401", "403", "429"]),
		new("GET", "/api/jobs/{nodeId}", ["401", "403", "404", "429"]),
		new("GET", "/api/jobs/{nodeId}/children", ["400", "401", "403", "404", "429"]),
		new("POST", "/api/jobs/{nodeId}/pickup", ["401", "403", "404", "409", "429"]),
		new("GET", "/api/jobs/{nodeId}/readiness", ["401", "403", "404", "429"]),
		new("GET", "/api/jobs/{nodeId}/sessions", ["400", "401", "403", "404", "429"]),
		new("POST", "/api/jobs/{nodeId}/sessions", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/jobs/{nodeId}/sessions/{sessionId}/finish", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/jobs/{nodeId}/sessions/{sessionId}/correct", ["400", "401", "403", "404", "409", "413", "429"]),
		new("GET", "/api/jobs/{nodeId}/prerequisites", ["400", "401", "403", "404", "429"]),
		new("POST", "/api/jobs/{nodeId}/prerequisites", ["400", "401", "403", "404", "409", "413", "429"]),
		new("DELETE", "/api/jobs/{nodeId}/prerequisites/{requiredJobId}", ["401", "403", "404", "429"]),
		new("GET", "/api/jobs/{nodeId}/achievement", ["401", "403", "404", "429"]),
		new("PUT", "/api/jobs/{nodeId}/achievement", ["400", "401", "403", "404", "409", "413", "429"]),
		new("GET", "/api/jobs/{nodeId}/cost", ["400", "401", "403", "404", "429"]),
		new("GET", "/api/jobs/{nodeId}/cost/hierarchy", ["400", "401", "403", "404", "429"]),
		new("GET", "/api/jobs/{nodeId}/subtree", ["400", "401", "403", "404", "429"]),
		new("GET", "/api/employees/{userId}/schedule", ["400", "401", "403", "404", "429"]),
		new("POST", "/api/employees/{userId}/schedule/versions", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/employees/{userId}/schedule/versions/{versionId}/correct", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/employees/{userId}/schedule/exceptions", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/employees/{userId}/schedule/exceptions/{exceptionId}/correct", ["400", "401", "403", "404", "409", "413", "429"]),
		new("GET", "/api/request-holding-areas", ["401", "403", "429"]),
		new("POST", "/api/requests", ["400", "401", "403", "404", "413", "429"]),
		new("GET", "/api/requests", ["401", "403", "429"]),
		new("GET", "/api/requests/{jobNodeId}", ["401", "403", "404", "409", "429"]),
		new("POST", "/api/requests/{jobNodeId}/comments", ["400", "401", "403", "404", "409", "413", "429"]),
		new("POST", "/api/requests/{jobNodeId}/acknowledge", ["401", "403", "404", "409", "429"]),
	];

	// Representative out-of-scope routes (ADR 0030): structural job commands, audit browsing, and
	// account administration. The exact-route-set test above already fails for any of these; these
	// are named explicitly per the remediation plan's own wording ("assert ... are absent").
	private static readonly string[] OutOfScopeRoutes = [
		"/api/jobs",
		"/api/jobs/{nodeId}/decompose",
		"/api/jobs/{nodeId}/archive",
		"/api/jobs/{nodeId}/move",
		"/api/audit",
		"/api/audit/events",
		"/api/users",
		"/api/employees/{userId}/role",
		"/api/employees/{userId}/enabled",
	];

	private static readonly string[] BannedSchemaSubstrings = [
		"PasswordHash",
		"SecurityStamp",
		"ResetToken",
		"ConcurrencyStamp",
		"IX_",
		"FK_",
		"UNIQUE constraint",
		"sqlite",
		"postgres",
	];

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private JsonObject document = null!;
	private TestWebApplicationFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();
		factory = new(database.ConnectionString);
		client = factory.CreateClient();

		// The OpenAPI document requires authentication like every other endpoint (remediation §2.5),
		// so this suite fetches it with a bearer token rather than an anonymous request.
		var seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.openapi-tests",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});
		await IdentityTestSupport.ClearRequiresPasswordChangeAsync(SchemaProvider.Sqlite, database.ConnectionString);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() },
			TargetUserId = bootstrap.AdministratorId,
			Label = "openapi-contract-tests",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
		request.Headers.Authorization = new("Bearer", issued.Token);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		response.StatusCode.Should().Be(HttpStatusCode.OK, $"OpenAPI fetch failed: {body}");
		document = JsonNode.Parse(body)!.AsObject();
		document["paths"].Should().NotBeNull("the response must be an OpenAPI document, not a login redirect or problem payload");
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
	public async Task An_unauthenticated_request_for_the_openapi_document_is_denied()
	{
		// factory.CreateClient() (unlike other suites in this project) follows redirects by
		// default, so an unauthenticated request lands on the login page rather than surfacing the
		// intermediate 302 -- asserting the final URL is the redirect evidence here.
		var response = await client.GetAsync("/openapi/v1.json");

		response.RequestMessage!.RequestUri!.PathAndQuery.Should().StartWith("/Account/Login");
	}

	[Fact]
	public void The_openapi_document_declares_exactly_the_accepted_external_route_set()
	{
		var actual = EnumerateOperations(document).Select(o => (o.Method, o.Path)).ToHashSet();
		var expected = ExpectedContract.Select(c => (c.Method, c.Path)).ToHashSet();

		actual.Should().BeEquivalentTo(expected);
	}

	[Fact]
	public void The_openapi_document_does_not_expose_structural_job_audit_or_account_administration_routes()
	{
		var paths = document["paths"]!.AsObject().Select(entry => entry.Key).ToArray();

		foreach (var outOfScopeRoute in OutOfScopeRoutes) {
			paths.Should().NotContain(outOfScopeRoute);
		}
	}

	[Fact]
	public void Every_operation_documents_its_expected_problem_response_status_codes()
	{
		foreach (var expected in ExpectedContract) {
			var operation = GetOperation(document, expected.Method, expected.Path);
			var responseKeys = operation["responses"]!.AsObject().Select(entry => entry.Key);
			var problemStatusCodes = responseKeys.Where(key => key.StartsWith('4')).ToHashSet();

			problemStatusCodes.Should().BeEquivalentTo(expected.ProblemStatusCodes,
				$"{expected.Method} {expected.Path} must document exactly its expected problem responses");
		}
	}

	[Fact]
	public void The_bearer_security_scheme_is_registered_and_required_by_every_operation()
	{
		var securityScheme = document["components"]?["securitySchemes"]?["Bearer"];
		securityScheme.Should().NotBeNull();
		securityScheme!["type"]!.GetValue<string>().Should().Be("http");
		securityScheme["scheme"]!.GetValue<string>().Should().Be("bearer");

		foreach (var expected in ExpectedContract) {
			var operation = GetOperation(document, expected.Method, expected.Path);
			var security = operation["security"]?.AsArray();

			security.Should().NotBeNullOrEmpty($"{expected.Method} {expected.Path} must require bearer authentication");
			security!.Any(requirement => requirement!.AsObject().ContainsKey("Bearer")).Should().BeTrue(
				$"{expected.Method} {expected.Path} must reference the Bearer security scheme");
		}
	}

	[Fact]
	public void The_problem_details_schema_has_the_standard_rfc7807_fields()
	{
		var schema = document["components"]!["schemas"]!["ProblemDetails"]!.AsObject();
		var properties = schema["properties"]!.AsObject().Select(entry => entry.Key).ToArray();

		properties.Should().Contain(["type", "title", "status", "detail"]);
	}

	[Fact]
	public void Growable_collection_operations_document_their_paging_parameters()
	{
		foreach (var (method, path) in new[] {
					 ("GET", "/api/jobs/search"), ("GET", "/api/jobs/{nodeId}/children"), ("GET", "/api/jobs/{nodeId}/sessions"),
					 ("GET", "/api/jobs/{nodeId}/prerequisites"),
				 }) {
			var parameterNames = GetParameterNames(document, method, path);

			parameterNames.Should().Contain(["offset", "pageSize"],
				$"{method} {path} is a growable collection and must publish the paging contract");
		}
	}

	[Fact]
	public void Cost_operations_document_their_explicit_result_size_bounds()
	{
		GetParameterNames(document, "GET", "/api/jobs/{nodeId}/cost")
			.Should().Contain("maxTraceSegments");
		GetParameterNames(document, "GET", "/api/jobs/{nodeId}/cost/hierarchy")
			.Should().Contain("maxHierarchyNodes");
		GetParameterNames(document, "GET", "/api/jobs/{nodeId}/subtree")
			.Should().Contain("depth", "ADR 0039's depth cap must be an explicit, callable-visible bound, not silently applied");
	}

	[Fact]
	public void The_openapi_document_does_not_expose_password_hashes_security_stamps_reset_tokens_or_provider_details()
	{
		var body = document.ToJsonString();

		foreach (var banned in BannedSchemaSubstrings) {
			body.Should().NotContain(banned, $"the OpenAPI document must not leak '{banned}'");
		}
	}

	private static IEnumerable<(string Method, string Path)> EnumerateOperations(JsonObject document)
	{
		foreach (var (path, operations) in document["paths"]!.AsObject()) {
			foreach (var (method, _) in operations!.AsObject()) {
				yield return (method.ToUpperInvariant(), path);
			}
		}
	}

	private static JsonObject GetOperation(JsonObject document, string method, string path)
	{
		var pathItem = document["paths"]![path]!.AsObject();
		var operation = pathItem[method.ToLowerInvariant()]
						?? throw new InvalidOperationException($"No {method} operation documented at {path}.");

		return operation.AsObject();
	}

	private static string[] GetParameterNames(JsonObject document, string method, string path)
	{
		var operation = GetOperation(document, method, path);

		return operation["parameters"]?.AsArray()
				   .Select(parameter => parameter!["name"]!.GetValue<string>())
				   .ToArray()
			   ?? [];
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

	private sealed class OperationContract(string method, string path, string[] problemStatusCodes)
	{
		public string Method { get; } = method;

		public string Path { get; } = path;

		public string[] ProblemStatusCodes { get; } = problemStatusCodes;
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
