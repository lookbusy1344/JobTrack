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
	private const byte FirstConcurrentCredentialId = 240;
	private const byte SecondConcurrentCredentialId = 241;

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
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			await AuditFailureInjection.InstallAsync(connection, Provider);
		}

		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest(seeded, CurrentPassword));

		await act.Should().ThrowAsync<DbUpdateException>();
		(await ReadStateAsync(seeded.AppUserId)).Should().Be(
			before,
			"the identity update, stamp rotation, and independently issued PAT revocation share the failed audit transaction");
	}

	[Fact]
	public async Task Adding_a_passkey_commits_the_row_rotates_stamps_revokes_pats_and_audits()
	{
		var seeded = await SeedCredentialStateAsync();
		var before = await ReadStateAsync(seeded.AppUserId);
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));

		var result = await sut.AddPasskeyAsync(CreateAddRequest(seeded, "Work MacBook", [1, 2, 3]));

		var after = await ReadStateAsync(seeded.AppUserId);
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(1);
		after.SecurityStamp.Should().NotBe(before.SecurityStamp);
		after.ConcurrencyStamp.Should().NotBe(before.ConcurrencyStamp);
		after.TokenIsRevoked.Should().BeTrue();
		(await CountAuditAsync(seeded.IdentityUserId, "authentication.passkey-added")).Should().Be(1);
		result.SecurityStamp.Should().Be(after.SecurityStamp);
	}

	[Fact]
	public async Task Passkey_add_audit_failure_rolls_back_the_row_stamps_and_pat_revocation()
	{
		var seeded = await SeedCredentialStateAsync();
		var before = await ReadStateAsync(seeded.AppUserId);
		await InstallAuditFailureAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));

		var act = () => sut.AddPasskeyAsync(CreateAddRequest(seeded, "Rollback key", [1, 2, 3]));

		await act.Should().ThrowAsync<DbUpdateException>();
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(0);
		(await ReadStateAsync(seeded.AppUserId)).Should().Be(before);
	}

	[Fact]
	public async Task Adding_a_passkey_with_a_duplicate_normalized_name_is_rejected_case_insensitively()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		_ = await sut.AddPasskeyAsync(CreateAddRequest(seeded, "Work MacBook", [1]));

		var act = () => sut.AddPasskeyAsync(CreateAddRequest(seeded, "work macbook", [2]));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("passkey-name-duplicate");
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(1);
	}

	[Fact]
	public async Task Concurrent_passkey_adds_with_the_same_normalized_name_allow_exactly_one_to_commit()
	{
		var seeded = await SeedCredentialStateAsync();

		var outcomes = await RunSimultaneouslyAsync(
			() => AddPasskeyCapturingFailureAsync(seeded, "Shared name", FirstConcurrentCredentialId),
			() => AddPasskeyCapturingFailureAsync(seeded, "shared name", SecondConcurrentCredentialId));

		outcomes.Count(outcome => outcome is null).Should().Be(1);
		var failure = outcomes.OfType<InvariantViolationException>().Should().ContainSingle().Subject;
		failure.ConstraintId.Should().Be("passkey-name-duplicate");
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(1);
	}

	[Fact]
	public async Task Adding_beyond_the_maximum_count_is_rejected()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		for (var index = 0; index < PasskeyPolicy.MaxPasskeysPerAccount; ++index) {
			_ = await sut.AddPasskeyAsync(CreateAddRequest(seeded, $"key {index}", [(byte)index]));
		}

		var act = () => sut.AddPasskeyAsync(CreateAddRequest(seeded, "one too many", [250]));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("passkey-max-count");
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(PasskeyPolicy.MaxPasskeysPerAccount);
	}

	[Fact]
	public async Task Concurrent_passkey_adds_competing_for_the_last_slot_allow_exactly_one_to_commit()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		for (var index = 0; index < PasskeyPolicy.MaxPasskeysPerAccount - 1; ++index) {
			_ = await sut.AddPasskeyAsync(CreateAddRequest(seeded, $"existing key {index}", [(byte)index]));
		}

		var outcomes = await RunSimultaneouslyAsync(
			() => AddPasskeyCapturingFailureAsync(seeded, "First contender", FirstConcurrentCredentialId),
			() => AddPasskeyCapturingFailureAsync(seeded, "Second contender", SecondConcurrentCredentialId));

		outcomes.Count(outcome => outcome is null).Should().Be(1);
		var failure = outcomes.OfType<InvariantViolationException>().Should().ContainSingle().Subject;
		failure.ConstraintId.Should().Be("passkey-max-count");
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(PasskeyPolicy.MaxPasskeysPerAccount);
	}

	[Fact]
	public async Task Adding_a_passkey_for_an_identity_user_the_actor_does_not_own_is_denied()
	{
		var seeded = await SeedCredentialStateAsync();
		var otherActor = await SeedExtraAppUserAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		var request = CreateAddRequest(seeded, "Work MacBook", [1]);
		var foreign = new AddPasskeyRequest {
			ActorUserId = otherActor,
			IdentityUserId = seeded.IdentityUserId,
			Name = request.Name,
			Credential = request.Credential,
			CorrelationId = request.CorrelationId,
		};

		var act = () => sut.AddPasskeyAsync(foreign);

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(0);
	}

	[Fact]
	public async Task Listing_returns_owned_passkeys_most_recent_first()
	{
		var seeded = await SeedCredentialStateAsync();
		var clock = new AdjustableClock(OperationInstant);
		var sut = CreatePort(database.ConnectionString, clock);
		_ = await sut.AddPasskeyAsync(CreateAddRequest(seeded, "Older", [1]));
		clock.Current += Duration.FromMinutes(5);
		_ = await sut.AddPasskeyAsync(CreateAddRequest(seeded, "Newer", [2]));

		var summaries = await sut.ListPasskeysAsync(new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
		});

		summaries.Select(s => s.Name).Should().Equal("Newer", "Older");
	}

	[Fact]
	public async Task Renaming_updates_the_name_without_rotating_stamps_or_revoking()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		var added = await sut.AddPasskeyAsync(CreateAddRequest(seeded, "Old name", [1]));
		var before = await ReadStateAsync(seeded.AppUserId);

		var result = await sut.RenamePasskeyAsync(new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
			CredentialId = added.CredentialId,
			NewName = "New name",
			CorrelationId = Guid.NewGuid(),
		});

		var after = await ReadStateAsync(seeded.AppUserId);
		result.Passkey.Name.Should().Be("New name");
		after.SecurityStamp.Should().Be(before.SecurityStamp);
		after.ConcurrencyStamp.Should().Be(before.ConcurrencyStamp);
		(await CountAuditAsync(seeded.IdentityUserId, "authentication.passkey-renamed")).Should().Be(1);
	}

	[Fact]
	public async Task Removing_a_passkey_rotates_stamps_revokes_pats_and_audits()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		var added = await sut.AddPasskeyAsync(CreateAddRequest(seeded, "To remove", [1]));
		var before = await ReadStateAsync(seeded.AppUserId);

		var result = await sut.RemovePasskeyAsync(new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
			CredentialId = added.CredentialId,
			CorrelationId = Guid.NewGuid(),
		});

		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(0);
		result.SecurityStamp.Should().NotBe(before.SecurityStamp);
		(await CountAuditAsync(seeded.IdentityUserId, "authentication.passkey-removed")).Should().Be(1);
	}

	[Fact]
	public async Task Passkey_remove_audit_failure_rolls_back_the_row_stamps_and_pat_revocation()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
		var added = await sut.AddPasskeyAsync(CreateAddRequest(seeded, "Rollback removal", [3, 2, 1]));
		var before = await ReadStateAsync(seeded.AppUserId);
		await InstallAuditFailureAsync();

		var act = () => sut.RemovePasskeyAsync(new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
			CredentialId = added.CredentialId,
			CorrelationId = Guid.NewGuid(),
		});

		await act.Should().ThrowAsync<DbUpdateException>();
		(await CountPasskeysAsync(seeded.IdentityUserId)).Should().Be(1);
		(await ReadStateAsync(seeded.AppUserId)).Should().Be(before);
	}

	[Fact]
	public async Task Removing_a_passkey_that_is_not_owned_is_not_found()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));

		var act = () => sut.RemovePasskeyAsync(new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
			CredentialId = Convert.ToBase64String([9, 9, 9]).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
			CorrelationId = Guid.NewGuid(),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Ensuring_the_user_handle_is_idempotent()
	{
		var seeded = await SeedCredentialStateAsync();
		var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));

		var first = await sut.EnsurePasskeyUserHandleAsync(CreateEnsureRequest(seeded));
		var second = await sut.EnsurePasskeyUserHandleAsync(CreateEnsureRequest(seeded));

		first.UserHandle.Should().NotBeNullOrWhiteSpace();
		second.UserHandle.Should().Be(first.UserHandle);
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
			Username = "credential.user",
			CurrentPassword = currentPassword,
			NewPassword = NewPassword,
			CorrelationId = Guid.NewGuid(),
		};

	private async Task<SeededCredentialState> SeedCredentialStateAsync()
	{
		await DeploySchemaAsync();
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

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
		identityCommand.AddParameter("@appUserId", appUserId.Value);
		identityCommand.AddParameter("@passwordHash", PasswordHasher.HashPassword(CredentialSubject, CurrentPassword));
		identityCommand.AddParameter("@securityStamp", Guid.NewGuid().ToString("N"));
		identityCommand.AddParameter("@concurrencyStamp", Guid.NewGuid().ToString("N"));
		var identityUserId = Convert.ToInt64(await identityCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		await using var tokenCommand = connection.CreateCommand();
		tokenCommand.CommandText = """
								   INSERT INTO personal_access_token
								     (app_user_id, token_hash, label, created_at, expires_at)
								   VALUES
								     (@appUserId, 'synthetic-token-hash', 'test token', @createdAt, @expiresAt);
								   """;
		tokenCommand.AddParameter("@appUserId", appUserId.Value);
		tokenCommand.AddParameter("@createdAt", FormatInstantForRawSql(OperationInstant - Duration.FromDays(1)));
		tokenCommand.AddParameter("@expiresAt", FormatInstantForRawSql(OperationInstant + Duration.FromDays(1)));
		_ = await tokenCommand.ExecuteNonQueryAsync();

		return new(appUserId, identityUserId);
	}

	private async Task<CredentialState> ReadStateAsync(AppUserId appUserId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
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
		command.AddParameter("@appUserId", appUserId.Value);
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
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
		await PostgreSqlTestInfrastructure.EnsureSecurityDefinerFunctionsAsync(connection, Provider);
	}

	private async Task InstallAuditFailureAsync()
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await AuditFailureInjection.InstallAsync(connection, Provider);
	}

	private async Task<Exception?> AddPasskeyCapturingFailureAsync(
		SeededCredentialState seeded, string name, byte credentialId)
	{
		try {
			var sut = CreatePort(database.ConnectionString, new AdjustableClock(OperationInstant));
			_ = await sut.AddPasskeyAsync(CreateAddRequest(seeded, name, [credentialId]));
			return null;
		}
		catch (Exception exception) {
			return exception;
		}
	}

	private static async Task<Exception?[]> RunSimultaneouslyAsync(
		Func<Task<Exception?>> first, Func<Task<Exception?>> second)
	{
		using var gate = new Barrier(2);
		return await Task.WhenAll(
			Task.Run(() => {
				gate.SignalAndWait();
				return first();
			}),
			Task.Run(() => {
				gate.SignalAndWait();
				return second();
			}));
	}

	private static AddPasskeyRequest CreateAddRequest(SeededCredentialState seeded, string name, byte[] credentialId) =>
		new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
			Name = name,
			Credential = new() {
				CredentialId = credentialId,
				PublicKey = new byte[] {
					10, 20, 30,
				},
				SignCount = 0,
				IsUserVerified = true,
				AttestationObject = new byte[] {
					1,
				},
				ClientDataJson = new byte[] {
					1,
				},
			},
			CorrelationId = Guid.NewGuid(),
		};

	private static EnsurePasskeyUserHandleRequest CreateEnsureRequest(SeededCredentialState seeded) =>
		new() {
			ActorUserId = seeded.AppUserId,
			IdentityUserId = seeded.IdentityUserId,
			CorrelationId = Guid.NewGuid(),
		};

	private async Task<AppUserId> SeedExtraAppUserAsync()
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES ('Other Synthetic User', 'Europe/London')
									 RETURNING id;
									 """;
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityCommand = connection.CreateCommand();
		identityCommand.CommandText = """
									  INSERT INTO identity_user
									    (app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
									     concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
									  VALUES
									    (@appUserId, 'other.user', 'OTHER.USER', @passwordHash, @securityStamp,
									     @concurrencyStamp, true, true, true, 0);
									  """;
		identityCommand.AddParameter("@appUserId", appUserId.Value);
		identityCommand.AddParameter("@passwordHash", PasswordHasher.HashPassword(CredentialSubject, CurrentPassword));
		identityCommand.AddParameter("@securityStamp", Guid.NewGuid().ToString("N"));
		identityCommand.AddParameter("@concurrencyStamp", Guid.NewGuid().ToString("N"));
		_ = await identityCommand.ExecuteNonQueryAsync();

		return appUserId;
	}

	private async Task<long> CountPasskeysAsync(long identityUserId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM identity_user_passkey WHERE identity_user_id = @id;";
		command.AddParameter("@id", identityUserId);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> CountAuditAsync(long identityUserId, string operation)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText =
			"SELECT COUNT(*) FROM audit_event WHERE operation = @operation AND entity_id = @id;";
		command.AddParameter("@operation", operation);
		command.AddParameter("@id", identityUserId);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
