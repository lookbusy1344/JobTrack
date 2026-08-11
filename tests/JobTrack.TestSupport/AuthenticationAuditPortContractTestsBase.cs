namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;

/// <summary>
///     Shared contract for <see cref="IAuthenticationAuditPort.RecordAsync" /> (fresh-eyes review §2.6),
///     asserted identically against PostgreSQL and SQLite by one thin sealed subclass per provider's own
///     test project — same shape as <see cref="PersonalAccessTokenPortContractTestsBase" />. Proves an
///     unknown-subject authentication failure records with a null actor rather than a fabricated or
///     display-name-collidable "system" <c>app_user</c>, and that concurrent unknown-login failures
///     never race to create or share any actor row (there is none to create).
/// </summary>
public abstract class AuthenticationAuditPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string LegacySystemActorDisplayName = "JobTrack authentication audit";
	private const int ConcurrentFailureCount = 8;

	private readonly IDisposableTestDatabase database;

	protected AuthenticationAuditPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Recording_a_known_login_success_stores_the_real_actor()
	{
		await DeploySchemaAsync();
		var (actorId, identityUserId) = await SeedAppUserAsync("Ada Lovelace");
		var sut = CreatePort(database.ConnectionString);

		await sut.RecordAsync(new() {
			ActorUserId = actorId,
			IdentityUserId = identityUserId,
			Kind = AuthenticationAuditEventKind.LoginSuccess,
			CorrelationId = Guid.NewGuid(),
		});

		var row = await SingleAuditRowAsync();
		row.ActorUserId.Should().Be(actorId.Value);
		row.EntityType.Should().Be("identity_user");
		row.EntityId.Should().Be(identityUserId);
	}

	[Fact]
	public async Task Recording_an_unknown_login_failure_stores_a_null_actor_with_a_redacted_subject_marker()
	{
		await DeploySchemaAsync();
		var sut = CreatePort(database.ConnectionString);

		await sut.RecordAsync(new() { Kind = AuthenticationAuditEventKind.LoginFailed, CorrelationId = Guid.NewGuid() });

		var row = await SingleAuditRowAsync();
		row.ActorUserId.Should().BeNull();
		row.EntityType.Should().Be("authentication_attempt");
		row.AfterData.Should().NotBeNull();
		row.AfterData.Should().Contain("redacted");
	}

	/// <summary>
	///     An administrator choosing an employee's display name is ordinary, non-unique user data (spec
	///     §16) -- it must never let an unrelated anonymous login failure attach to that employee, which
	///     is exactly what the removed display-name lookup allowed.
	/// </summary>
	[Fact]
	public async Task An_employee_sharing_the_former_system_actor_display_name_is_never_attributed_an_unknown_login_failure()
	{
		await DeploySchemaAsync();
		var (collidingActorId, _) = await SeedAppUserAsync(LegacySystemActorDisplayName);
		var sut = CreatePort(database.ConnectionString);

		await sut.RecordAsync(new() { Kind = AuthenticationAuditEventKind.LoginFailed, CorrelationId = Guid.NewGuid() });

		var row = await SingleAuditRowAsync();
		row.ActorUserId.Should().NotBe(collidingActorId.Value);
		row.ActorUserId.Should().BeNull();
	}

	[Fact]
	public async Task Concurrent_unknown_login_failures_are_all_recorded_without_creating_any_actor_row()
	{
		await DeploySchemaAsync();
		var sut = CreatePort(database.ConnectionString);
		var appUserCountBefore = await CountAppUsersAsync();

		await Task.WhenAll(Enumerable.Range(0, ConcurrentFailureCount).Select(_ => sut.RecordAsync(new() {
			Kind = AuthenticationAuditEventKind.LoginFailed,
			CorrelationId = Guid.NewGuid(),
		})));

		var rows = await AllAuditRowsAsync();
		rows.Should().HaveCount(ConcurrentFailureCount);
		rows.Should().OnlyContain(row => row.ActorUserId == null);
		(await CountAppUsersAsync()).Should().Be(appUserCountBefore, "an unknown-subject failure creates no actor row to race over");
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IAuthenticationAuditPort CreatePort(string connectionString);

	private async Task DeploySchemaAsync()
	{
		await using var connection = await OpenExistingConnectionAsync();
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private async Task<(AppUserId ActorId, long IdentityUserId)> SeedAppUserAsync(string displayName)
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
										     @concurrencyStamp, @requiresPasswordChange, @isEnabled, @lockoutEnabled, 0)
										  RETURNING id;
										  """;
		AddParameter(identityUserCommand, "@appUserId", appUserId.Value);
		AddParameter(identityUserCommand, "@userName", "seeded.user");
		AddParameter(identityUserCommand, "@normalizedUserName", "SEEDED.USER");
		AddParameter(identityUserCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@requiresPasswordChange", false);
		AddParameter(identityUserCommand, "@isEnabled", true);
		AddParameter(identityUserCommand, "@lockoutEnabled", true);
		var identityUserId = Convert.ToInt64(await identityUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		return (appUserId, identityUserId);
	}

	private async Task<long> CountAppUsersAsync()
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM app_user;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<AuditRow> SingleAuditRowAsync()
	{
		var rows = await AllAuditRowsAsync();
		return rows.Should().ContainSingle().Subject;
	}

	private async Task<IReadOnlyList<AuditRow>> AllAuditRowsAsync()
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT actor_user_id, entity_type, entity_id, after_data FROM audit_event ORDER BY id;";

		var rows = new List<AuditRow>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			rows.Add(new(
				reader.IsDBNull(0) ? null : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
				reader.GetString(1),
				Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
				reader.IsDBNull(3) ? null : reader.GetString(3)));
		}

		return rows;
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

	private sealed record AuditRow(long? ActorUserId, string EntityType, long EntityId, string? AfterData);
}
