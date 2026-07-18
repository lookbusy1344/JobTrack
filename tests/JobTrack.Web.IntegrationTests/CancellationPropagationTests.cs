namespace JobTrack.Web.IntegrationTests;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP proof that request cancellation reaches the <see cref="IJobTrackClient" /> call for
///     one read and one mutation (remediation plan §3.7). A <see cref="DispatchProxy" /> around the
///     registered client intercepts one method's <see cref="CancellationToken" /> argument and awaits it
///     via <c>Task.Delay(Timeout.Infinite, token)</c> instead of ever calling the real implementation --
///     the only way that delay ends is the ASP.NET Core request-aborted token firing, so observing the
///     resulting <see cref="OperationCanceledException" /> is direct evidence the handler's
///     <see cref="CancellationToken" /> parameter (bound to <c>HttpContext.RequestAborted</c>) was
///     actually passed through to the library call, not silently dropped.
/// </summary>
public sealed partial class CancellationPropagationTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private readonly CancellationHook hook = new();
	private AppUserId administratorId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId rootId;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.cancel-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
		rootId = bootstrap.RootJobNodeId;

		factory = new(database.ConnectionString, hook);
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
	public async Task Cancelling_a_read_request_propagates_cancellation_into_the_library_call()
	{
		_ = await SeedEmployeeAsync("cancel.read.worker");
		var authCookie = await SignInAsync("cancel.read.worker");
		hook.TargetMethodName = nameof(IJobQueries.GetJobNodeAsync);

		using var cts = new CancellationTokenSource();
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{rootId.Value}");
		request.Headers.Add("Cookie", authCookie);
		cts.CancelAfter(TimeSpan.FromMilliseconds(100));

		var act = () => client.SendAsync(request, cts.Token);
		await act.Should().ThrowAsync<OperationCanceledException>();

		// TaskCompletionSource.Task is designed to be awaited from a context other than the one
		// that completes it -- VSTHRD003 does not special-case this well-known pattern.
#pragma warning disable VSTHRD003
		var observed = await Task.WhenAny(hook.Observed.Task, Task.Delay(TimeSpan.FromSeconds(5))) == hook.Observed.Task;
#pragma warning restore VSTHRD003
		observed.Should().BeTrue("the library call must observe the same cancellation as the HTTP request, not run to completion regardless");
	}

	[Fact]
	public async Task Cancelling_a_mutation_request_propagates_cancellation_into_the_library_call()
	{
		var workerId = await SeedEmployeeAsync("cancel.mutation.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("cancel.mutation.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		hook.TargetMethodName = nameof(IWorkCommands.StartSessionAsync);

		using var cts = new CancellationTokenSource();
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{leafId.Value}/sessions");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add("X-CSRF-TOKEN", antiforgeryToken);
		request.Content = new StringContent($$"""{ "workedByUserId": {{workerId.Value}} }""", Encoding.UTF8, "application/json");
		cts.CancelAfter(TimeSpan.FromMilliseconds(100));

		var act = () => client.SendAsync(request, cts.Token);
		await act.Should().ThrowAsync<OperationCanceledException>();

		// TaskCompletionSource.Task is designed to be awaited from a context other than the one
		// that completes it -- VSTHRD003 does not special-case this well-known pattern.
#pragma warning disable VSTHRD003
		var observed = await Task.WhenAny(hook.Observed.Task, Task.Delay(TimeSpan.FromSeconds(5))) == hook.Observed.Task;
#pragma warning restore VSTHRD003
		observed.Should().BeTrue("the library call must observe the same cancellation as the HTTP request, not run to completion regardless");
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = result.Id,
		});

		return result.Id;
	}

	private async Task<AppUserId> SeedEmployeeAsync(string userName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		var appUserId = (long)(await insertAppUser.ExecuteScalarAsync())!;

		var placeholderUser = new JobTrackIdentityUser {
			AppUserId = new(appUserId),
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = string.Empty,
			SecurityStamp = Guid.NewGuid().ToString(),
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(placeholderUser, KnownPassword);

		await using var insertIdentityUser = connection.CreateCommand();
		insertIdentityUser.CommandText = """
										 INSERT INTO identity_user
										 	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										 	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										 VALUES
										 	($appUserId, $userName, $normalizedUserName, $passwordHash, $securityStamp,
										 	 $concurrencyStamp, 0, 1, 1, 0);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", placeholderUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();

		await using var insertRole = connection.CreateCommand();
		insertRole.CommandText =
			"INSERT INTO identity_user_role (identity_user_id, identity_role_id) SELECT id, $roleId FROM identity_user WHERE app_user_id = $appUserId;";
		_ = insertRole.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)EmployeeRole.Worker);
		_ = await insertRole.ExecuteNonQueryAsync();

		return new(appUserId);
	}

	private async Task<string> SignInAsync(string userName)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return ExtractCookiePair(authCookie);
	}

	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in login page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<(string CookieHeader, string Token)> GetAntiforgeryTokenAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery-token");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in token response.");
		var token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()
					?? throw new InvalidOperationException("No antiforgery token in token response.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

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

	/// <summary>Mutable state shared between the test and the proxy so one factory instance can drive both tests.</summary>
	private sealed class CancellationHook
	{
		public string TargetMethodName { get; set; } = "";

		public TaskCompletionSource<bool> Observed { get; } = new();
	}

	private sealed class TestWebApplicationFactory(string identityConnectionString, CancellationHook hook) : WebApplicationFactory<Program>
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
					return new CancellationObservingJobTrackClient(real, hook);
				});
			});
		}
	}

	/// <summary>
	///     Forwards every member to the real client except <see cref="Query" />/<see cref="Work" />, whose
	///     returned interfaces are wrapped by <see cref="CancellationObservingProxy{TInterface}" /> so the
	///     single method named by <see cref="CancellationHook.TargetMethodName" /> awaits its
	///     <see cref="CancellationToken" /> argument instead of running the real implementation.
	/// </summary>
	private sealed class CancellationObservingJobTrackClient(IJobTrackClient inner, CancellationHook hook) : IJobTrackClient
	{
		public IInstallationCommands Installation => inner.Installation;

		public IJobQueries Query { get; } = CancellationObservingProxy<IJobQueries>.Wrap(inner.Query, hook);

		public IEmployeeCommands Employees => inner.Employees;

		public IJobCommands Jobs => inner.Jobs;

		public IWorkCommands Work { get; } = CancellationObservingProxy<IWorkCommands>.Wrap(inner.Work, hook);

		public IScheduleCommands Schedules => inner.Schedules;

		public IRateCommands Rates => inner.Rates;

		public ICostQueries Costs => inner.Costs;

		public IAuditQueries Audit => inner.Audit;

		public ITokenCommands Tokens => inner.Tokens;

		public IRequestCommands Requests => inner.Requests;

		public IAuthenticationAuditCommands AuthenticationAudit => inner.AuthenticationAudit;

		public IAccountCredentialCommands Credentials => inner.Credentials;
	}

	/// <summary>
	///     A <see cref="DispatchProxy" /> that forwards every call to the real implementation, except: if
	///     the invoked method's name matches <see cref="CancellationHook.TargetMethodName" />, it ignores
	///     the real implementation entirely and instead awaits the call's <see cref="CancellationToken" />
	///     argument via <c>Task.Delay(Timeout.Infinite, token)</c>, resolving <see cref="CancellationHook.Observed" />
	///     when that delay is canceled.
	/// </summary>
	// CA1852 wants this sealed since it has no compile-time subtypes, but DispatchProxy.Create
	// generates a runtime-emitted subtype and throws if the base type is sealed -- the analyzer
	// cannot see that requirement.
#pragma warning disable CA1852
	private class CancellationObservingProxy<TInterface> : DispatchProxy where TInterface : class
#pragma warning restore CA1852
	{
		private CancellationHook _hook = null!;
		private TInterface _inner = null!;

		public static TInterface Wrap(TInterface inner, CancellationHook hook)
		{
			var created = Create<TInterface, CancellationObservingProxy<TInterface>>();
			var proxy = (CancellationObservingProxy<TInterface>)(object)created!;
			proxy._inner = inner;
			proxy._hook = hook;
			return created;
		}

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			if (targetMethod is not null && targetMethod.Name == _hook.TargetMethodName && args is not null) {
				var tokenIndex = Array.FindIndex(args, a => a is CancellationToken);
				if (tokenIndex >= 0) {
					var token = (CancellationToken)args[tokenIndex]!;
					var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
					var method = typeof(CancellationObservingProxy<TInterface>)
						.GetMethod(nameof(AwaitCancellationAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
						.MakeGenericMethod(resultType);
					return method.Invoke(this, [token]);
				}
			}

			return targetMethod!.Invoke(_inner, args);
		}

		private async Task<TResult> AwaitCancellationAsync<TResult>(CancellationToken token)
		{
			try {
				await Task.Delay(Timeout.Infinite, token);
			}
			catch (OperationCanceledException) {
				_hook.Observed.TrySetResult(true);
				throw;
			}

			return default!;
		}
	}
}
