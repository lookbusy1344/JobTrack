namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Shared provider contract for the atomic self-service password transition (remediation plan
///     §2.2): password/hash state, forced-change state, stamps, PAT revocation, and audit persistence
///     either commit together or remain unchanged.
/// </summary>
public abstract class AccountCredentialPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string CurrentPassword = "current123";
	private const string NewPassword = "replacement456";

	private static readonly Instant OperationInstant = Instant.FromUtc(2026, 7, 23, 12, 0);
	private static readonly PasswordHasher<EmployeeCredentialSubject> PasswordHasher = new();
	private static readonly EmployeeCredentialSubject CredentialSubject = new();

	private readonly IDisposableTestDatabase database;

	protected AccountCredentialPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Incorrect_current_password_rejects_before_any_persistent_state_changes()
	{
		var seeded = await SeedCredentialStateAsync();
		var before = await ReadStateAsync(seeded.AppUserId);
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));

		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest(seeded, "incorrect-password"));

		await act.Should().ThrowAsync<InvariantViolationException>()
			.WithMessage("*current password is incorrect*");
		(await ReadStateAsync(seeded.AppUserId)).Should().Be(before);
	}

	[Fact]
	public async Task Password_change_commits_hash_flag_stamps_pat_revocation_and_one_audit_event()
	{
		var seeded = await SeedCredentialStateAsync();
		var before = await ReadStateAsync(seeded.AppUserId);
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));

		var result = await sut.ChangeOwnPasswordAsync(CreateRequest(seeded, CurrentPassword));

		var after = await ReadStateAsync(seeded.AppUserId);
		PasswordHasher.VerifyHashedPassword(CredentialSubject, after.PasswordHash, CurrentPassword)
			.Should().Be(PasswordVerificationResult.Failed);
		PasswordHasher.VerifyHashedPassword(CredentialSubject, after.PasswordHash, NewPassword)
			.Should().NotBe(PasswordVerificationResult.Failed);
		after.RequiresPasswordChange.Should().BeFalse();
		after.SecurityStamp.Should().NotBe(before.SecurityStamp);
		after.ConcurrencyStamp.Should().NotBe(before.ConcurrencyStamp);
		after.TokenIsRevoked.Should().BeTrue();
		after.PasswordChangeAuditCount.Should().Be(1);
		result.SecurityStamp.Should().Be(after.SecurityStamp);
		result.ConcurrencyStamp.Should().Be(after.ConcurrencyStamp);
	}

	[Fact]
	public async Task Audit_persistence_failure_rolls_back_every_earlier_password_transition_step()
	{
		var seeded = await SeedCredentialStateAsync();
		var before = await ReadStateAsync(seeded.AppUserId);
		await using (var connection = await OpenExistingConnectionAsync()) {
			await AuditFailureInjection.InstallAsync(connection, Provider);
		}

		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest(seeded, CurrentPassword));

		await act.Should().ThrowAsync<DbUpdateException>();
		(await ReadStateAsync(seeded.AppUserId)).Should().Be(
			before,
			"the identity update, stamp rotation, and independently issued PAT revocation share the failed audit transaction");
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract object FormatInstantForRawSql(Instant instant);

	internal abstract IAccountCredentialPort CreatePort(string connectionString, IClock clock);

	private static ChangeOwnPasswordRequest CreateRequest(SeededCredentialState seeded, string currentPassword) =>
		new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
			CurrentPassword = currentPassword,
			NewPassword = NewPassword,
			CorrelationId = Guid.NewGuid(),
		};

	private async Task<SeededCredentialState> SeedCredentialStateAsync()
	{
		await DeploySchemaAsync();
		await using var connection = await OpenExistingConnectionAsync();

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES ('Synthetic Credential User', 'Europe/London')
									 RETURNING id;
									 """;
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityCommand = connection.CreateCommand();
		identityCommand.CommandText = """
									  INSERT INTO identity_user
									    (app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
									     concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
									  VALUES
									    (@appUserId, 'credential.user', 'CREDENTIAL.USER', @passwordHash, @securityStamp,
									     @concurrencyStamp, true, true, true, 0)
									  RETURNING id;
									  """;
		AddParameter(identityCommand, "@appUserId", appUserId.Value);
		AddParameter(identityCommand, "@passwordHash", PasswordHasher.HashPassword(CredentialSubject, CurrentPassword));
		AddParameter(identityCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		var identityUserId = Convert.ToInt64(await identityCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		await using var tokenCommand = connection.CreateCommand();
		tokenCommand.CommandText = """
								   INSERT INTO personal_access_token
								     (app_user_id, token_hash, label, created_at, expires_at)
								   VALUES
								     (@appUserId, 'synthetic-token-hash', 'test token', @createdAt, @expiresAt);
								   """;
		AddParameter(tokenCommand, "@appUserId", appUserId.Value);
		AddParameter(tokenCommand, "@createdAt", FormatInstantForRawSql(OperationInstant - Duration.FromDays(1)));
		AddParameter(tokenCommand, "@expiresAt", FormatInstantForRawSql(OperationInstant + Duration.FromDays(1)));
		_ = await tokenCommand.ExecuteNonQueryAsync();

		return new(appUserId, identityUserId);
	}

	private async Task<CredentialState> ReadStateAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT iu.password_hash,
							         iu.requires_password_change,
							         iu.security_stamp,
							         iu.concurrency_stamp,
							         CASE WHEN pat.revoked_at IS NULL THEN 0 ELSE 1 END,
							         (SELECT COUNT(*) FROM audit_event ae
							          WHERE ae.operation = 'authentication.password-change'
							            AND ae.entity_id = iu.id)
							  FROM identity_user iu
							  JOIN personal_access_token pat ON pat.app_user_id = iu.app_user_id
							  WHERE iu.app_user_id = @appUserId;
							  """;
		AddParameter(command, "@appUserId", appUserId.Value);
		await using var reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue();

		return new(
			reader.GetString(0),
			reader.GetBoolean(1),
			reader.GetString(2),
			reader.GetString(3),
			Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
			Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture));
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = await OpenExistingConnectionAsync();
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
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

	private sealed record SeededCredentialState(AppUserId AppUserId, long IdentityUserId);

	private sealed record CredentialState(
		string PasswordHash,
		bool RequiresPasswordChange,
		string SecurityStamp,
		string ConcurrencyStamp,
		bool TokenIsRevoked,
		long PasswordChangeAuditCount);
}
