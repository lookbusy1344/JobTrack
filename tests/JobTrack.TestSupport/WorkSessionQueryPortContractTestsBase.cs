namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="IWorkSessionQueryPort" /> (plan §8.5 slice 4), asserted
///     identically against PostgreSQL and SQLite by one thin sealed subclass per provider's own test
///     project -- same shape as <see cref="JobBrowseQueryPortContractTestsBase" />. Seeds a leaf with
///     attached <c>LeafWork</c> and two historical sessions via the real
///     <see cref="IInstallationBootstrapPort" />/<see cref="IJobNodeCommandPort" />/<see cref="IWorkSessionCommandPort" />,
///     then corrects both sessions to known instants so ordering assertions are deterministic
///     regardless of real-clock resolution.
/// </summary>
public abstract class WorkSessionQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected WorkSessionQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetSessionsAsync_returns_the_workers_sessions_most_recent_first()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 2, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, workerId);

		result.Sessions.Select(s => s.StartedAt).Should()
			.ContainInOrder(Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 1, 8, 0));
	}

	[Fact]
	public async Task GetSessionsAsync_bounds_results_by_offset_and_limit_preserving_most_recent_first_order()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 2, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var firstPage = await port.GetSessionsAsync(administratorId, leafId, workerId, 0, 1);
		var secondPage = await port.GetSessionsAsync(administratorId, leafId, workerId, 1, 1);

		firstPage.Sessions.Select(s => s.StartedAt).Should().ContainSingle().Which.Should().Be(Instant.FromUtc(2026, 1, 2, 8, 0));
		secondPage.Sessions.Select(s => s.StartedAt).Should().ContainSingle().Which.Should().Be(Instant.FromUtc(2026, 1, 1, 8, 0));
	}

	[Fact]
	public async Task GetSessionsAsync_does_not_return_another_workers_sessions()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, administratorId);

		result.Sessions.Should().BeEmpty();
	}

	/// <summary>
	///     A <see langword="null" /> worker filter means "every worker's sessions on this leaf" (ADR 0041)
	///     — the default the sessions panel now loads with, narrowing to one worker being the follow-up
	///     filter rather than the entry point. Ordering must stay most-recent-first across the union of
	///     workers, not merely within one worker's own sessions.
	/// </summary>
	[Fact]
	public async Task GetSessionsAsync_without_a_worker_filter_returns_every_workers_sessions_most_recent_first()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		await SeedCorrectedSessionAsync(administratorId, administratorId, leafId, Instant.FromUtc(2026, 1, 2, 8, 0),
			Instant.FromUtc(2026, 1, 2, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, null);

		result.Sessions.Should().HaveCount(2);
		result.Sessions.Select(s => s.WorkedByUserId).Should().Contain([workerId, administratorId]);
		result.Sessions.Select(s => s.StartedAt).Should()
			.ContainInOrder(Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 1, 8, 0));
	}

	[Fact]
	public async Task GetSessionsAsync_returns_the_actors_current_roles()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, workerId);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetSessionsAsync_throws_for_a_nonexistent_actor()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetSessionsAsync(new(administratorId.Value + 999), leafId, workerId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetSessionsAsync_throws_for_a_nonexistent_leaf()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetSessionsAsync(administratorId, new(leafId.Value + 999), workerId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetActiveSessionsAsync_returns_the_actors_own_unfinished_session_among_the_given_leaves()
	{
		var (_, workerId, leafId) = await SeedWorkedLeafAsync();
		var sessionCommandPort = CreateSessionCommandPort(database.ConnectionString);
		var active = await sessionCommandPort.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(workerId, [leafId]);

		result.Sessions.Should().ContainSingle(s => s.Id == active.Id);
	}

	[Fact]
	public async Task GetActiveSessionsAsync_does_not_return_a_finished_session()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(workerId, [leafId]);

		result.Sessions.Should().BeEmpty();
	}

	/// <summary>
	///     The port itself applies no actor-based filtering (matching <see cref="GetSessionsAsync" />'s
	///     "every worker" default, ADR 0041) -- it returns every unfinished session among the given
	///     leaves regardless of who is querying or who worked it. <c>JobQueries</c> is the layer that
	///     narrows this to what the querying actor may see.
	/// </summary>
	[Fact]
	public async Task GetActiveSessionsAsync_returns_every_workers_active_session_among_the_given_leaves()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var sessionCommandPort = CreateSessionCommandPort(database.ConnectionString);
		var active = await sessionCommandPort.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(administratorId, [leafId]);

		result.Sessions.Should().ContainSingle(s => s.Id == active.Id);
	}

	[Fact]
	public async Task GetActiveSessionsAsync_with_no_leaves_returns_an_empty_result_without_throwing()
	{
		var (_, workerId, _) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(workerId, []);

		result.Sessions.Should().BeEmpty();
	}

	[Fact]
	public async Task GetActiveSessionsAsync_returns_the_actors_current_roles()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(administratorId, [leafId]);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateJobCommandPort(string connectionString);

	protected abstract IWorkSessionCommandPort CreateSessionCommandPort(string connectionString);

	protected abstract IWorkSessionQueryPort CreateQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	/// <summary>Seeds root -&gt; leaf "Pour foundation" (worker-owned, LeafWork attached), owned and worked by a seeded worker.</summary>
	private async Task<(AppUserId AdministratorId, AppUserId WorkerId, JobNodeId LeafId)> SeedWorkedLeafAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper", EmployeeRole.Worker);

		var jobCommandPort = CreateJobCommandPort(database.ConnectionString);
		var leaf = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Pour foundation",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobCommandPort.AttachLeafWorkAsync(new() { Context = ContextFor(administratorId), JobNodeId = leaf.Id });

		return (administratorId, workerId, leaf.Id);
	}

	private async Task SeedCorrectedSessionAsync(
		AppUserId administratorId, AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var sessionCommandPort = CreateSessionCommandPort(database.ConnectionString);
		var started = await sessionCommandPort.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var finished = await sessionCommandPort.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = started.Id,
			Version = started.Version,
		});
		_ = await sessionCommandPort.CorrectSessionAsync(new() {
			Context = ContextFor(administratorId),
			SessionId = finished.Id,
			StartedAt = startedAt,
			FinishedAt = finishedAt,
			Reason = "Backdated for deterministic test ordering.",
			Version = finished.Version,
		});
	}

	private async Task<AppUserId> SeedEmployeeAsync(string displayName, string userName, EmployeeRole role)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityUserCommand = connection.CreateCommand();
		identityUserCommand.CommandText = """
										  INSERT INTO identity_user
										  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										  VALUES
										  	(@appUserId, @userName, @normalizedUserName, 'test-hash', @securityStamp,
										  	 @concurrencyStamp, @requiresPasswordChange, @isEnabled, @lockoutEnabled, 0);
										  """;
		AddParameter(identityUserCommand, "@appUserId", appUserId.Value);
		AddParameter(identityUserCommand, "@userName", userName);
		AddParameter(identityUserCommand, "@normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@requiresPasswordChange", false);
		AddParameter(identityUserCommand, "@isEnabled", true);
		AddParameter(identityUserCommand, "@lockoutEnabled", true);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();

		return appUserId;
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
