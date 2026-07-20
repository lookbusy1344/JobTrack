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
///     Shared contract for <see cref="IPersonalAccessTokenPort.IssueAsync" /> (security review
///     remediation §2.1), asserted identically against PostgreSQL and SQLite by one thin sealed
///     subclass per provider's own test project — same shape as
///     <see cref="EmployeeCommandPortContractTestsBase" />. Proves issuance reloads the actor's current
///     <c>identity_user</c> row and denies disabled/locked actors, matching list/revoke/revoke-all's
///     existing account-state enforcement.
/// </summary>
public abstract class PersonalAccessTokenPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private static readonly Instant CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0);
	private static readonly Instant ExpiresAt = Instant.FromUtc(2026, 2, 1, 0, 0);
	private static readonly Instant FarFutureLockoutEnd = Instant.FromUtc(2099, 1, 1, 0, 0);
	private static readonly Duration OneTick = Duration.FromTicks(1);

	private readonly IDisposableTestDatabase database;

	protected PersonalAccessTokenPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	[Fact]
	public async Task An_enabled_actor_can_issue_a_token_for_themselves()
	{
		var actorId = await SeedEmployeeAsync("worker.enabled", true, null);
		var sut = CreatePort(database.ConnectionString);

		var result = await sut.IssueAsync(new() {
			Context = ContextFor(actorId),
			TargetUserId = actorId,
			Label = "laptop",
			TokenHash = "hash-1",
			CreatedAt = CreatedAt,
			ExpiresAt = ExpiresAt,
		});

		result.Label.Should().Be("laptop");
	}

	[Fact]
	public async Task A_disabled_actor_cannot_issue_a_token_for_themselves()
	{
		var actorId = await SeedEmployeeAsync("worker.disabled", false, null);
		var sut = CreatePort(database.ConnectionString);

		var act = () => sut.IssueAsync(new() {
			Context = ContextFor(actorId),
			TargetUserId = actorId,
			Label = "laptop",
			TokenHash = "hash-2",
			CreatedAt = CreatedAt,
			ExpiresAt = ExpiresAt,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_locked_out_actor_cannot_issue_a_token_for_themselves()
	{
		var actorId = await SeedEmployeeAsync("worker.locked", true, FarFutureLockoutEnd);
		var sut = CreatePort(database.ConnectionString);

		var act = () => sut.IssueAsync(new() {
			Context = ContextFor(actorId),
			TargetUserId = actorId,
			Label = "laptop",
			TokenHash = "hash-3",
			CreatedAt = CreatedAt,
			ExpiresAt = ExpiresAt,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_actor_may_issue_a_token_at_the_exact_lockout_end_boundary()
	{
		var actorId = await SeedEmployeeAsync("worker.lockout-boundary", true, CreatedAt);
		var sut = CreatePort(database.ConnectionString);

		var result = await sut.IssueAsync(new() {
			Context = ContextFor(actorId),
			TargetUserId = actorId,
			Label = "boundary",
			TokenHash = "hash-lockout-boundary",
			CreatedAt = CreatedAt,
			ExpiresAt = ExpiresAt,
		});

		result.CreatedAt.Should().Be(CreatedAt);
	}

	[Fact]
	public async Task An_administrator_cannot_issue_a_token_for_another_user()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await SeedEmployeeAsync("worker.other", true, null);
		var sut = CreatePort(database.ConnectionString);

		var act = () => sut.IssueAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = workerId,
			Label = "laptop",
			TokenHash = "hash-4",
			CreatedAt = CreatedAt,
			ExpiresAt = ExpiresAt,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Token_authentication_uses_one_injected_clock_read_at_the_expiry_boundary()
	{
		var actorId = await SeedEmployeeAsync("worker.expiry-boundary", true, null);
		var clock = new AdjustableClock(ExpiresAt - OneTick);
		var sut = CreatePort(database.ConnectionString, clock);
		_ = await sut.IssueAsync(new() {
			Context = ContextFor(actorId),
			TargetUserId = actorId,
			Label = "boundary",
			TokenHash = "hash-boundary",
			CreatedAt = CreatedAt,
			ExpiresAt = ExpiresAt,
		});

		clock.ResetReadCount();
		var beforeExpiry = await sut.TryAuthenticateAsync("hash-boundary");

		beforeExpiry.Should().NotBeNull();
		clock.ReadCount.Should().Be(1);

		clock.Current = ExpiresAt;
		clock.ResetReadCount();
		var atExpiry = await sut.TryAuthenticateAsync("hash-boundary");

		atExpiry.Should().BeNull();
		clock.ReadCount.Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IPersonalAccessTokenPort CreatePort(string connectionString);

	protected abstract IPersonalAccessTokenPort CreatePort(string connectionString, IClock clock);

	/// <summary>
	///     Formats an <see cref="Instant" /> for a raw ADO.NET parameter matching the
	///     provider's <c>lockout_end</c> column representation (PostgreSQL <c>timestamptz</c> vs.
	///     SQLite's signed 64-bit UTC tick count).
	/// </summary>
	protected abstract object FormatInstantForRawSql(Instant instant);

	private async Task EnsureSchemaDeployedAsync()
	{
		await using var connection = await OpenExistingConnectionAsync();
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private async Task<AppUserId> SeedAdministratorAsync()
	{
		await EnsureSchemaDeployedAsync();

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace.pat-issuance",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		return result.AdministratorId;
	}

	private async Task<AppUserId> SeedEmployeeAsync(string userName, bool isEnabled, Instant? lockoutEnd)
	{
		await EnsureSchemaDeployedAsync();

		await using var connection = await OpenExistingConnectionAsync();

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", userName);
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityUserCommand = connection.CreateCommand();
		identityUserCommand.CommandText = """
										  INSERT INTO identity_user
										  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, lockout_end, access_failed_count)
										  VALUES
										  	(@appUserId, @userName, @normalizedUserName, 'test-hash', @securityStamp,
										  	 @concurrencyStamp, false, @isEnabled, @lockoutEnabled, @lockoutEnd, 0);
										  """;
		AddParameter(identityUserCommand, "@appUserId", appUserId.Value);
		AddParameter(identityUserCommand, "@userName", userName);
		AddParameter(identityUserCommand, "@normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@isEnabled", isEnabled);
		AddParameter(identityUserCommand, "@lockoutEnabled", lockoutEnd is not null);
		AddParameter(identityUserCommand, "@lockoutEnd", lockoutEnd.HasValue ? FormatInstantForRawSql(lockoutEnd.Value) : DBNull.Value);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)EmployeeRole.Worker);
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
