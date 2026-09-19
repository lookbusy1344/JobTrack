namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Reflection;
using Abstractions;
using Application;
using AwesomeAssertions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pages;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Host-level coverage for the remediation plan's Stage 2 follow-up: a
///     <see cref="TransientPersistenceException" /> that escapes a library call must reach the API
///     caller as a 503 with <c>Retry-After</c> (<see cref="JobTrackApi.ExecuteAsync" />), never a 409
///     invariant or an unhandled 500. The page-side counterpart
///     (<see cref="ErrorModel.OnGet" />) is exercised directly, since the exception-handler middleware
///     that would route a real page request there is disabled in the Development factory every other
///     integration test relies on.
/// </summary>
public sealed class TransientPersistenceFailureTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private readonly FaultHook hook = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId rootId;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.transient-failure-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrap.RootJobNodeId;

		factory = new(database.ConnectionString, hook);
		client = factory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
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
	public async Task A_transient_persistence_failure_from_the_library_reaches_the_api_caller_as_503_with_retry_after()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "transient.api.worker");
		var authCookie = await client.SignInAsync("transient.api.worker");
		hook.TargetMethodName = nameof(IJobQueries.GetJobNodeAsync);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{rootId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		response.Headers.RetryAfter.Should().NotBeNull();
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("/problems/transient-failure");
	}

	[Fact]
	public void ErrorModel_reports_a_transient_failure_when_the_exception_handler_caught_one()
	{
		var httpContext = new DefaultHttpContext();
		httpContext.Features.Set<IExceptionHandlerFeature>(new TestExceptionHandlerFeature(new TransientPersistenceException()));
		var model = new ErrorModel {
			PageContext = new() {
				HttpContext = httpContext,
			},
		};

		model.OnGet();

		model.IsTransientFailure.Should().BeTrue();
	}

	[Fact]
	public void ErrorModel_does_not_report_a_transient_failure_for_an_unrelated_exception()
	{
		var httpContext = new DefaultHttpContext();
		httpContext.Features.Set<IExceptionHandlerFeature>(new TestExceptionHandlerFeature(new InvalidOperationException("boom")));
		var model = new ErrorModel {
			PageContext = new() {
				HttpContext = httpContext,
			},
		};

		model.OnGet();

		model.IsTransientFailure.Should().BeFalse();
	}

	// Path/Endpoint access no instance data -- CA1822 wants them static, but they implement
	// IExceptionHandlerFeature, which requires instance members.
#pragma warning disable CA1822
	private sealed class TestExceptionHandlerFeature(Exception error) : IExceptionHandlerFeature
	{

		public IExceptionHandlerPathFeature? Endpoint => null;
		public Exception Error { get; } = error;

		public string Path => "";
	}
#pragma warning restore CA1822

	/// <summary>Mutable state shared between the test and the proxy so one factory instance can drive one fault at a time.</summary>
	private sealed class FaultHook
	{
		public string TargetMethodName { get; set; } = "";
	}

	private sealed class TestWebApplicationFactory(string identityConnectionString, FaultHook hook) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.ConfigureTestServices(services => {
				var descriptor = services.Single(d => d.ServiceType == typeof(IJobTrackClient));
				_ = services.Remove(descriptor);
				_ = services.AddSingleton<IJobTrackClient>(_ => {
					var real = JobTrackSqlite.Create(identityConnectionString);
					return new FaultInjectingJobTrackClient(real, hook);
				});
			});
		}
	}

	/// <summary>
	///     Forwards every member to the real client except <see cref="Query" />, whose returned interface
	///     is wrapped by <see cref="FaultInjectingProxy{TInterface}" /> so the single method named by
	///     <see cref="FaultHook.TargetMethodName" /> throws <see cref="TransientPersistenceException" />
	///     instead of running the real implementation -- the same shape a deadlock or a busy database
	///     would surface as after 2.2's classifier runs.
	/// </summary>
	private sealed class FaultInjectingJobTrackClient(IJobTrackClient inner, FaultHook hook) : IJobTrackClient
	{
		public IInstallationCommands Installation => inner.Installation;

		public IJobQueries Query { get; } = FaultInjectingProxy<IJobQueries>.Wrap(inner.Query, hook);

		public IEmployeeCommands Employees => inner.Employees;

		public IJobCommands Jobs => inner.Jobs;

		public IWorkCommands Work => inner.Work;

		public IScheduleCommands Schedules => inner.Schedules;

		public IRateCommands Rates => inner.Rates;

		public ICostQueries Costs => inner.Costs;

		public IAuditQueries Audit => inner.Audit;

		public ITokenCommands Tokens => inner.Tokens;

		public IRequestCommands Requests => inner.Requests;

		public IAuthenticationAuditCommands AuthenticationAudit => inner.AuthenticationAudit;

		public IAccountCredentialCommands Credentials => inner.Credentials;
	}

	// CA1852 wants this sealed since it has no compile-time subtypes, but DispatchProxy.Create
	// generates a runtime-emitted subtype and throws if the base type is sealed -- the analyzer
	// cannot see that requirement.
#pragma warning disable CA1852
	private class FaultInjectingProxy<TInterface> : DispatchProxy where TInterface : class
#pragma warning restore CA1852
	{
		private FaultHook _hook = null!;
		private TInterface _inner = null!;

		public static TInterface Wrap(TInterface inner, FaultHook hook)
		{
			var created = Create<TInterface, FaultInjectingProxy<TInterface>>();
			var proxy = (FaultInjectingProxy<TInterface>)(object)created!;
			proxy._inner = inner;
			proxy._hook = hook;
			return created;
		}

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			if (targetMethod is not null && targetMethod.Name == _hook.TargetMethodName) {
				throw new TransientPersistenceException();
			}

			return targetMethod!.Invoke(_inner, args);
		}
	}
}
