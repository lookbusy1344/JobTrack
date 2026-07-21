namespace JobTrack.Web.EndToEndTests;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Abstractions;
using Application;
using Database;
using Domain.Schedules;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Hosts the real JobTrack.Web application as a real child process listening on a real Kestrel
///     socket (not <c>WebApplicationFactory</c>'s in-memory <c>TestServer</c>, which a real browser
///     cannot navigate to) and drives it with a real Chromium browser via Playwright, for plan
///     §8.5/§8.7 responsive, keyboard, focus, and accessibility evidence (fix-plan §2.5). Abstract over
///     <see cref="Provider" /> so every browser-test class runs its full workflow against both supported
///     providers (plan §8.7: "end-to-end tests cover the complete operational scenarios in both
///     PostgreSQL and SQLite configurations") -- see <see cref="SqliteBrowserFixture" /> and
///     <see cref="PostgreSqlBrowserFixture" /> for the two concrete instantiations.
/// </summary>
/// <remarks>
///     Requires a one-time <c>playwright install chromium</c> browser-binary download outside this
///     repo's usual <c>dotnet restore</c>/<c>dotnet build</c> -- see
///     <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class BrowserFixture : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	public const string AdministratorUserName = "admin.browser.e2e";
	public const string AdministratorPassword = "Browser-Horse-Battery-42!";

	private const string CertificatePassword = "browser-e2e-cert";
	private const int LoginRateLimitPermitLimitForTests = 500;
	private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(200);

	private readonly IDisposableTestDatabase database;
	private readonly StringBuilder processOutput = new();
	private string? certificatePath;
	private IPlaywright playwright = null!;
	private NpgsqlDataSource? postgreSqlDataSource;
	private IJobTrackClient seedClient = null!;
	private Process? webProcess;

	protected BrowserFixture(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	// Cross-browser compatibility (plan §8.7) is sampled against one representative fixture per
	// engine (FirefoxBrowserFixture/WebKitBrowserFixture, both SQLite), not the full
	// provider-and-viewport matrix already proven under Chromium -- rendering-engine differences
	// are orthogonal to database provider, so doubling every workflow across all three engines
	// would add runtime without adding signal.
	protected virtual BrowserEngine Engine => BrowserEngine.Chromium;

	public IBrowser Browser { get; private set; } = null!;

	public string BaseAddress { get; private set; } = string.Empty;

	public AppUserId AdministratorId { get; private set; }

	public JobNodeId RootJobNodeId { get; private set; }

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedClient = CreateSeedClient();
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = AdministratorUserName,
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		AdministratorId = bootstrap.AdministratorId;
		RootJobNodeId = bootstrap.RootJobNodeId;
		await ClearRequiresPasswordChangeAsync();

		var port = GetFreeLoopbackPort();
		BaseAddress = $"https://127.0.0.1:{port}";
		certificatePath = WriteSelfSignedCertificate();
		StartWebProcess(port, certificatePath);
		await WaitForReadinessAsync();

		playwright = await Playwright.CreateAsync();
		var browserType = Engine switch {
			BrowserEngine.Chromium => playwright.Chromium,
			BrowserEngine.Firefox => playwright.Firefox,
			BrowserEngine.WebKit => playwright.Webkit,
			_ => throw new ArgumentOutOfRangeException(nameof(Engine), Engine, "Unsupported browser engine."),
		};
		Browser = await browserType.LaunchAsync(new() { Headless = true });
	}

	public async Task DisposeAsync()
	{
		await Browser.CloseAsync();
		Dispose();

		if (postgreSqlDataSource is not null) {
			await postgreSqlDataSource.DisposeAsync();
			postgreSqlDataSource = null;
		}

		await database.DisposeAsync();
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		// xUnit calls both IAsyncLifetime.DisposeAsync (which calls this) and IDisposable.Dispose
		// separately during cleanup, so this must tolerate running twice: fields are nulled out
		// after their first teardown rather than left pointing at already-disposed objects.
		playwright?.Dispose();
		playwright = null!;

		if (webProcess is { HasExited: false }) {
			webProcess.Kill(true);
			webProcess.WaitForExit((int)ReadinessTimeout.TotalMilliseconds);
		}

		webProcess?.Dispose();
		webProcess = null;

		if (certificatePath is not null && File.Exists(certificatePath)) {
			File.Delete(certificatePath);
		}

		certificatePath = null;
	}

	public Task<IBrowserContext> NewContextAsync(int width, int height) =>
		Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, IgnoreHTTPSErrors = true });

	public async Task<JobNodeId> SeedLeafAsync(string description, JobNodeId? parentId = null)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId ?? RootJobNodeId,
			Description = description,
			OwnerUserId = AdministratorId,
			Priority = Priority.Medium,
		});
		return result.Id;
	}

	public async Task<JobNodeId> SeedBranchAsync(string description, JobNodeId? parentId = null)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId ?? RootJobNodeId,
			Description = description,
			OwnerUserId = AdministratorId,
			Priority = Priority.Medium,
		});
		return result.Id;
	}

	/// <summary>Seeds an <see cref="EmployeeRole.Requester" /> employee for requester-page browser tests (ADR 0034).</summary>
	public async Task<AppUserId> SeedRequesterAsync(string userName, string password)
	{
		var result = await seedClient.Employees.CreateEmployeeAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			DisplayName = userName,
			IanaTimeZone = "Etc/UTC",
			UserName = userName,
			Password = password,
			Role = EmployeeRole.Requester,
		});
		await ClearRequiresPasswordChangeAsync();

		return result.Id;
	}

	/// <summary>
	///     Seeds an active, globally eligible holding area owned by the administrator, for requester-page
	///     browser tests (ADR 0033/0034).
	/// </summary>
	public async Task<RequestHoldingAreaId> SeedHoldingAreaAsync()
	{
		const short priorityMedium = 2;

		switch (Provider) {
			case SchemaProvider.Sqlite: {
					await using var connection = new SqliteConnection(database.ConnectionString);
					await connection.OpenAsync();

					await using var insertNode = connection.CreateCommand();
					insertNode.CommandText = """
										 INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
										 VALUES ($parentId, 'Holding area', $ownerId, $ownerId, $priorityId, $postedAt);
										 SELECT last_insert_rowid();
										 """;
					_ = insertNode.Parameters.AddWithValue("$parentId", RootJobNodeId.Value);
					_ = insertNode.Parameters.AddWithValue("$ownerId", AdministratorId.Value);
					_ = insertNode.Parameters.AddWithValue("$priorityId", priorityMedium);
					_ = insertNode.Parameters.AddWithValue("$postedAt", DateTimeOffset.UtcNow.UtcTicks - DateTime.UnixEpoch.Ticks);
					var jobNodeId = (long)(await insertNode.ExecuteScalarAsync())!;

					await using var insertHoldingArea = connection.CreateCommand();
					insertHoldingArea.CommandText = """
												INSERT INTO request_holding_area (job_node_id, name, default_priority_id, is_active)
												VALUES ($jobNodeId, 'IT Intake', $priorityId, 1);
												SELECT last_insert_rowid();
												""";
					_ = insertHoldingArea.Parameters.AddWithValue("$jobNodeId", jobNodeId);
					_ = insertHoldingArea.Parameters.AddWithValue("$priorityId", priorityMedium);
					return new((long)(await insertHoldingArea.ExecuteScalarAsync())!);
				}
			case SchemaProvider.PostgreSql: {
					await using var connection = new NpgsqlConnection(database.ConnectionString);
					await connection.OpenAsync();

					await using var insertNode = connection.CreateCommand();
					insertNode.CommandText = """
										 INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
										 VALUES (@parentId, 'Holding area', @ownerId, @ownerId, @priorityId, now())
										 RETURNING id;
										 """;
					insertNode.Parameters.AddWithValue("parentId", RootJobNodeId.Value);
					insertNode.Parameters.AddWithValue("ownerId", AdministratorId.Value);
					insertNode.Parameters.AddWithValue("priorityId", priorityMedium);
					var jobNodeId = (long)(await insertNode.ExecuteScalarAsync())!;

					await using var insertHoldingArea = connection.CreateCommand();
					insertHoldingArea.CommandText = """
												INSERT INTO request_holding_area (job_node_id, name, default_priority_id, is_active)
												VALUES (@jobNodeId, 'IT Intake', @priorityId, true)
												RETURNING id;
												""";
					insertHoldingArea.Parameters.AddWithValue("jobNodeId", jobNodeId);
					insertHoldingArea.Parameters.AddWithValue("priorityId", priorityMedium);
					return new((long)(await insertHoldingArea.ExecuteScalarAsync())!);
				}
			default:
				throw new ArgumentOutOfRangeException(nameof(Provider), Provider, "Unsupported provider.");
		}
	}

	/// <summary>Submits a request as <paramref name="requesterId" />, for requester-page browser tests (ADR 0033/0034).</summary>
	public async Task<JobRequestResult> SubmitRequestAsync(AppUserId requesterId, RequestHoldingAreaId holdingAreaId, string description) =>
		await seedClient.Requests.SubmitAsync(new() {
			Context = new() { Actor = requesterId, CorrelationId = Guid.NewGuid() },
			HoldingAreaId = holdingAreaId,
			Description = description,
		});

	public async Task SeedPrerequisiteAsync(JobNodeId requiredJobId, JobNodeId dependentJobId) =>
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredJobId,
			DependentJobId = dependentJobId,
		});

	public async Task<(JobNodeId LeafId, WorkSessionId SessionId, long Version)> SeedFinishedSessionAsync(string leafDescription)
	{
		var leafId = await SeedLeafAsync(leafDescription);
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
		});
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = AdministratorId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
		});
		return (leafId, finished.Id, finished.Version);
	}

	/// <summary>Seeds one worked leaf with the requested number of distinct active workers.</summary>
	public async Task<(JobNodeId LeafId, IReadOnlyList<string> WorkerDisplayNames)> SeedActiveSessionsAsync(
		string leafDescription, int workerCount)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);

		var leafId = await SeedLeafAsync(leafDescription);
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
		});
		var workerDisplayNames = new List<string>(workerCount);
		for (var index = 0; index < workerCount; index++) {
			var displayName = $"Active Worker {index + 1}";
			var employee = await seedClient.Employees.CreateEmployeeAsync(new() {
				Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
				DisplayName = displayName,
				IanaTimeZone = "Etc/UTC",
				UserName = $"active.worker.{Guid.NewGuid():N}",
				Password = AdministratorPassword,
				Role = EmployeeRole.Worker,
			});
			_ = await seedClient.Work.StartSessionAsync(new() {
				Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
				LeafWorkId = leafId,
				WorkedByUserId = employee.Id,
			});
			workerDisplayNames.Add(displayName);
		}

		await ClearRequiresPasswordChangeAsync();
		return (leafId, workerDisplayNames);
	}

	/// <summary>
	///     Bootstrap already provisions the administrator with an open-ended default schedule version
	///     starting <see cref="EmployeeProvisioningDefaults.ScheduleEffectiveStart" /> (2020-01-01,
	///     <see langword="null" /> end), so a second version must be fully bounded before that date to
	///     avoid the same-employee overlap constraint.
	/// </summary>
	public async Task<ScheduleVersionId> SeedScheduleVersionAsync(LocalDate effectiveStart)
	{
		var result = await seedClient.Schedules.AddScheduleVersionAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			UserId = AdministratorId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"], effectiveStart,
				effectiveStart.PlusMonths(1),
				[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]),
		});
		return result.Id;
	}

	public async Task<ScheduleExceptionId> SeedScheduleExceptionAsync(Instant start, Instant end)
	{
		var result = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			UserId = AdministratorId,
			Entry = new(ScheduleExceptionEffect.RemoveWorkingTime, new(start, end), null),
			Reason = "Accessibility fixture",
		});
		return result.Id;
	}

	/// <summary>
	///     Other browser tests in this shared fixture add an open-ended user cost rate for the
	///     administrator starting 2026-01-01, so this must be fully bounded before that date to avoid
	///     the same-employee overlap constraint.
	/// </summary>
	public async Task<UserCostRateId> SeedUserCostRateAsync(Instant effectiveStart)
	{
		var result = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			UserId = AdministratorId,
			Rate = new(new(25m), effectiveStart, effectiveStart.Plus(Duration.FromDays(30))),
		});
		return result.Id;
	}

	public async Task<NodeRateOverrideId> SeedNodeRateOverrideAsync(Instant effectiveStart)
	{
		var result = await seedClient.Rates.AddNodeRateOverrideAsync(new() {
			Context = new() { Actor = AdministratorId, CorrelationId = Guid.NewGuid() },
			UserId = AdministratorId,
			Override = new(RootJobNodeId, new(40m), effectiveStart, null),
		});
		return result.Id;
	}

	private IJobTrackClient CreateSeedClient() => Provider switch {
		SchemaProvider.Sqlite => JobTrackSqlite.Create(database.ConnectionString),
		SchemaProvider.PostgreSql => JobTrackPostgreSql.Create(
			postgreSqlDataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build()),
		_ => throw new ArgumentOutOfRangeException(nameof(Provider), Provider, "Unsupported provider."),
	};

	private void StartWebProcess(int port, string certPath)
	{
		var webAssemblyPath = typeof(Program).Assembly.Location;
		var startInfo = new ProcessStartInfo {
			FileName = "dotnet",
			ArgumentList = { webAssemblyPath },
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(webAssemblyPath),
		};
		startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
		startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = BaseAddress;
		startInfo.EnvironmentVariables["Database__Provider"] = Provider.ToString();
		startInfo.EnvironmentVariables["ConnectionStrings__JobTrackIdentity"] = database.ConnectionString;
		startInfo.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = certPath;
		startInfo.EnvironmentVariables["Kestrel__Certificates__Default__Password"] = CertificatePassword;

		// One process is shared across an entire browser-test class (many sequential
		// /Account/Login GET+POST pairs via SignInAsync), which exceeds the unconfigured
		// production login-rate-limit budget within its 60s window. Raised for this child
		// process only -- production keeps the unconfigured 20-permit/60s default.
		startInfo.EnvironmentVariables["RateLimiting__LoginPermitLimit"] =
			LoginRateLimitPermitLimitForTests.ToString(CultureInfo.InvariantCulture);

		webProcess = new() { StartInfo = startInfo, EnableRaisingEvents = true };
		webProcess.OutputDataReceived += (_, args) => {
			if (args.Data is not null) {
				lock (processOutput) {
					_ = processOutput.AppendLine(args.Data);
				}
			}
		};
		webProcess.ErrorDataReceived += (_, args) => {
			if (args.Data is not null) {
				lock (processOutput) {
					_ = processOutput.AppendLine(args.Data);
				}
			}
		};

		_ = webProcess.Start();
		webProcess.BeginOutputReadLine();
		webProcess.BeginErrorReadLine();
	}

	private async Task WaitForReadinessAsync()
	{
		using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
		using var probeClient = new HttpClient(handler);
		var deadline = DateTime.UtcNow + ReadinessTimeout;

		while (DateTime.UtcNow < deadline) {
			if (webProcess is { HasExited: true }) {
				throw new InvalidOperationException($"The JobTrack.Web process exited early (code {webProcess.ExitCode}). Output:\n{processOutput}");
			}

			try {
				var response = await probeClient.GetAsync($"{BaseAddress}/Account/Login");
				if (response.IsSuccessStatusCode) {
					return;
				}
			}
			catch (HttpRequestException) {
				// Not listening yet; keep polling until the deadline.
			}

			await Task.Delay(ReadinessPollInterval);
		}

		throw new TimeoutException($"JobTrack.Web did not become ready within {ReadinessTimeout}. Output so far:\n{processOutput}");
	}

	private static int GetFreeLoopbackPort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private static string WriteSelfSignedCertificate()
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

		var path = Path.Combine(Path.GetTempPath(), $"jobtrack-browser-e2e-{Guid.NewGuid():N}.pfx");
		File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
		return path;
	}

	// The bootstrap command always leaves the administrator with RequiresPasswordChange set (spec
	// §8.1 forced-first-change), and RequiresPasswordChangePageFilter redirects every page for such
	// an account regardless of the requested URL. That flow is already covered by the integration
	// suite's AccountFlowTests -- browser tests need a signed-in account free to reach the feature
	// pages under test, so this clears the flag directly rather than re-proving the redirect here.
	private async Task ClearRequiresPasswordChangeAsync()
	{
		switch (Provider) {
			case SchemaProvider.Sqlite:
				await using (var connection = new SqliteConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					await using var command = connection.CreateCommand();
					command.CommandText = "UPDATE identity_user SET requires_password_change = 0;";
					_ = await command.ExecuteNonQueryAsync();
				}

				break;
			case SchemaProvider.PostgreSql:
				await using (var connection = new NpgsqlConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					await using var command = connection.CreateCommand();
					command.CommandText = "UPDATE identity_user SET requires_password_change = false;";
					_ = await command.ExecuteNonQueryAsync();
				}

				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(Provider), Provider, "Unsupported provider.");
		}
	}

	private async Task DeploySchemaAsync()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));

		switch (Provider) {
			case SchemaProvider.Sqlite:
				await using (var connection = new SqliteConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					await using (var pragma = connection.CreateCommand()) {
						pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
						_ = await pragma.ExecuteNonQueryAsync();
					}

					var deployer = new SchemaDeployer(
						connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
					await deployer.DeployAsync(scripts, CancellationToken.None);
				}

				break;
			case SchemaProvider.PostgreSql:
				await using (var connection = new NpgsqlConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					var deployer = new SchemaDeployer(
						connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
					await deployer.DeployAsync(scripts, CancellationToken.None);
				}

				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(Provider), Provider, "Unsupported provider.");
		}
	}
}

/// <summary>SQLite instantiation of <see cref="BrowserFixture" /> -- see that type for rationale.</summary>
public sealed class SqliteBrowserFixture() : BrowserFixture(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;
}

/// <summary>PostgreSQL instantiation of <see cref="BrowserFixture" /> -- see that type for rationale.</summary>
public sealed class PostgreSqlBrowserFixture() : BrowserFixture(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;
}

/// <summary>Firefox instantiation of <see cref="BrowserFixture" /> -- see that type for rationale.</summary>
public sealed class FirefoxBrowserFixture() : BrowserFixture(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;

	protected override BrowserEngine Engine => BrowserEngine.Firefox;
}

/// <summary>WebKit instantiation of <see cref="BrowserFixture" /> -- see that type for rationale.</summary>
public sealed class WebKitBrowserFixture() : BrowserFixture(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;

	protected override BrowserEngine Engine => BrowserEngine.WebKit;
}

/// <summary>The Playwright-supported rendering engines this project samples cross-browser compatibility against.</summary>
public enum BrowserEngine
{
	Chromium,
	Firefox,
	WebKit,
}
