namespace JobTrack.Identity.Tests;

using System.Globalization;
using Abstractions;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using TestSupport;

/// <summary>
///     Exercises the <see cref="IUserPasskeyStore{TUser}" /> half of <see cref="JobTrackUserStore" />
///     against a real schema-deployed SQLite database (ADR 0022, plan §7.2): credential round trip,
///     deterministic ordering, cross-account isolation, defensive copying, monotonic assertion updates,
///     and cancellation. Each assertion re-opens a fresh context so the round trip goes through
///     persistence, not in-memory object state.
/// </summary>
public sealed class JobTrackUserStorePasskeyTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private readonly IDataProtectionProvider dataProtectionProvider = new EphemeralDataProtectionProvider();
	private readonly SqliteDatabaseFixture database = new();

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();
	}

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task AddOrUpdate_stores_a_new_passkey_resolvable_by_credential_id()
	{
		var user = await CreateUserAsync("passkey.owner");
		var credentialId = new byte[] {
			1, 2, 3, 4,
		};

		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(user, Info(credentialId, "Work MacBook"), CancellationToken.None));

		var owner = await WithStoreAsync(store => store.FindByPasskeyIdAsync(credentialId, CancellationToken.None));
		owner.Should().NotBeNull();
		owner!.Id.Should().Be(user.Id);
	}

	[Fact]
	public async Task FindByPasskeyIdAsync_returns_null_for_an_unknown_credential()
	{
		var owner = await WithStoreAsync(store => store.FindByPasskeyIdAsync([9, 9, 9], CancellationToken.None));
		owner.Should().BeNull();
	}

	[Fact]
	public async Task FindPasskeyAsync_round_trips_the_stored_credential_material()
	{
		var user = await CreateUserAsync("passkey.material");
		var credentialId = new byte[] {
			10, 20, 30,
		};
		var createdAt = new DateTimeOffset(2027, 3, 4, 5, 6, 7, TimeSpan.Zero);
		var info = new UserPasskeyInfo(
			credentialId, [40, 41, 42], createdAt, 7,
			["internal", "hybrid"], true, true, false,
			[50, 51], [60, 61]) {
			Name = "Blue YubiKey",
		};

		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(user, info, CancellationToken.None));

		var reloaded = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		reloaded.Should().NotBeNull();
		reloaded!.CredentialId.Should().Equal(credentialId);
		reloaded.PublicKey.Should().Equal(40, 41, 42);
		reloaded.CreatedAt.Should().Be(createdAt);
		reloaded.SignCount.Should().Be(7);
		reloaded.Transports.Should().Equal("internal", "hybrid");
		reloaded.IsUserVerified.Should().BeTrue();
		reloaded.IsBackupEligible.Should().BeTrue();
		reloaded.IsBackedUp.Should().BeFalse();
		reloaded.AttestationObject.Should().Equal(50, 51);
		reloaded.ClientDataJson.Should().Equal(60, 61);
		reloaded.Name.Should().Be("Blue YubiKey");
	}

	[Fact]
	public async Task GetPasskeysAsync_returns_all_owned_credentials_newest_first()
	{
		var user = await CreateUserAsync("passkey.list");
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info([1], "Older", createdAt: Instant.FromUtc(2026, 1, 1, 0, 0).ToDateTimeOffset()), CancellationToken.None));
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info([2], "Newer", createdAt: Instant.FromUtc(2026, 6, 1, 0, 0).ToDateTimeOffset()), CancellationToken.None));

		var passkeys = await WithStoreAsync(store => store.GetPasskeysAsync(user, CancellationToken.None));

		passkeys.Select(p => p.Name).Should().ContainInOrder("Newer", "Older");
	}

	[Fact]
	public async Task GetPasskeysAsync_excludes_another_accounts_credentials()
	{
		var owner = await CreateUserAsync("passkey.mine");
		var other = await CreateUserAsync("passkey.theirs");
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(owner, Info([1], "Mine"), CancellationToken.None));
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(other, Info([2], "Theirs"), CancellationToken.None));

		var passkeys = await WithStoreAsync(store => store.GetPasskeysAsync(owner, CancellationToken.None));

		passkeys.Should().ContainSingle().Which.Name.Should().Be("Mine");
	}

	[Fact]
	public async Task FindPasskeyAsync_does_not_return_another_accounts_credential()
	{
		var owner = await CreateUserAsync("passkey.a");
		var other = await CreateUserAsync("passkey.b");
		var credentialId = new byte[] {
			7, 7,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(owner, Info(credentialId, "Owned"), CancellationToken.None));

		var seenByOther = await WithStoreAsync(store => store.FindPasskeyAsync(other, credentialId, CancellationToken.None));
		seenByOther.Should().BeNull();
	}

	[Fact]
	public async Task AddOrUpdate_advances_sign_count_and_backup_state_on_an_existing_credential()
	{
		var user = await CreateUserAsync("passkey.assert");
		var credentialId = new byte[] {
			3, 3, 3,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info(credentialId, "Phone", 5, isBackedUp: false), CancellationToken.None));

		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info(credentialId, "ignored-on-update", 9, isBackedUp: true), CancellationToken.None));

		var reloaded = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		reloaded!.SignCount.Should().Be(9);
		reloaded.IsBackedUp.Should().BeTrue();
		reloaded.Name.Should().Be("Phone", "an assertion update must not rewrite the friendly name");
	}

	[Fact]
	public async Task AddOrUpdate_rejects_a_replayed_or_regressed_nonzero_signature_counter()
	{
		var user = await CreateUserAsync("passkey.rollback");
		var credentialId = new byte[] {
			4, 4,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(user, Info(credentialId, "Key", 20), CancellationToken.None));

		var replay = () => WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info(credentialId, "Key", 20), CancellationToken.None));
		var regression = () => WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info(credentialId, "Key", 3), CancellationToken.None));

		await replay.Should().ThrowAsync<InvalidOperationException>();
		await regression.Should().ThrowAsync<InvalidOperationException>();
		var reloaded = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		reloaded!.SignCount.Should().Be(20);
	}

	[Fact]
	public async Task AddOrUpdate_does_not_restore_a_credential_removed_after_assertion_verification()
	{
		var user = await CreateUserAsync("passkey.concurrent-reset");
		var credentialId = new byte[] {
			4, 3, 2, 1,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info(credentialId, "Reset key", 7), CancellationToken.None));
		var verifiedPasskey = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));

		await RemovePasskeyAndRotateStampsAsync(user.Id, credentialId);
		var assertionUpdate = Info(
			credentialId, verifiedPasskey!.Name!, 8, isBackedUp: verifiedPasskey.IsBackedUp, createdAt: verifiedPasskey.CreatedAt);
		var act = () => WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, assertionUpdate, CancellationToken.None));

		await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
		var restored = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		restored.Should().BeNull("a verified assertion from before reset must not recreate the removed credential");
	}

	[Fact]
	public async Task AddOrUpdate_accepts_repeated_zero_signature_counters_for_synced_passkeys()
	{
		var user = await CreateUserAsync("passkey.zero-counter");
		var credentialId = new byte[] {
			4, 5,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info(credentialId, "Synced"), CancellationToken.None));

		var act = () => WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info(credentialId, "Synced", isBackedUp: true), CancellationToken.None));

		await act.Should().NotThrowAsync();
		var reloaded = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		reloaded!.IsBackedUp.Should().BeTrue();
	}

	[Fact]
	public async Task AddOrUpdate_refuses_to_reassign_a_credential_to_another_account()
	{
		var owner = await CreateUserAsync("passkey.holder");
		var thief = await CreateUserAsync("passkey.thief");
		var credentialId = new byte[] {
			5, 5,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(owner, Info(credentialId, "Held"), CancellationToken.None));

		var act = () => WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(thief, Info(credentialId, "Stolen"), CancellationToken.None));

		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task AddOrUpdate_rejects_a_credential_without_user_verification()
	{
		var user = await CreateUserAsync("passkey.unverified");

		var act = () => WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, Info([6], "NoUV", isUserVerified: false), CancellationToken.None));

		await act.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task RemovePasskeyAsync_deletes_the_owned_credential()
	{
		var user = await CreateUserAsync("passkey.remove");
		var credentialId = new byte[] {
			8, 8,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(user, Info(credentialId, "Doomed"), CancellationToken.None));

		await WithStoreAsync(store => store.RemovePasskeyAsync(user, credentialId, CancellationToken.None));

		var reloaded = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		reloaded.Should().BeNull();
	}

	[Fact]
	public async Task RemovePasskeyAsync_does_not_delete_another_accounts_credential()
	{
		var owner = await CreateUserAsync("passkey.keep");
		var other = await CreateUserAsync("passkey.other");
		var credentialId = new byte[] {
			9, 1,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(owner, Info(credentialId, "Kept"), CancellationToken.None));

		await WithStoreAsync(store => store.RemovePasskeyAsync(other, credentialId, CancellationToken.None));

		var stillThere = await WithStoreAsync(store => store.FindPasskeyAsync(owner, credentialId, CancellationToken.None));
		stillThere.Should().NotBeNull();
	}

	[Fact]
	public async Task Returned_key_material_is_a_defensive_copy()
	{
		var user = await CreateUserAsync("passkey.defensive");
		var credentialId = new byte[] {
			2, 4, 6,
		};
		await WithStoreAsync(store => store.AddOrUpdatePasskeyAsync(
			user, new(credentialId, [11, 22, 33], DateTimeOffset.UnixEpoch, 1,
				[], true, false, false,
				[1], [2]) {
				Name = "Mutable",
			}, CancellationToken.None));

		var first = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		first!.PublicKey[0] ^= 0xFF;
		first.CredentialId[0] ^= 0xFF;

		var second = await WithStoreAsync(store => store.FindPasskeyAsync(user, credentialId, CancellationToken.None));
		second!.PublicKey.Should().Equal(11, 22, 33);
		second.CredentialId.Should().Equal(2, 4, 6);
	}

	[Fact]
	public async Task FindByIdAsync_resolves_a_user_by_their_passkey_user_handle()
	{
		var user = await CreateUserAsync("passkey.handle");
		const string handle = "DrJ9h667yZ-DAlq0kUrjdacbo-95j_Rn04XwuW6O1Yw";
		await SetPasskeyUserHandleAsync(user.Id, handle);

		var byHandle = await WithStoreAsync(store => store.FindByIdAsync(handle, CancellationToken.None));
		var byNumericId = await WithStoreAsync(store => store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None));

		byHandle.Should().NotBeNull();
		byHandle!.Id.Should().Be(user.Id);
		byNumericId.Should().NotBeNull();
		byNumericId!.Id.Should().Be(user.Id);
	}

	[Fact]
	public async Task GetUserIdAsync_returns_the_passkey_user_handle_when_one_exists()
	{
		var user = await CreateUserAsync("passkey.identity-id");
		const string handle = "DrJ9h667yZ-DAlq0kUrjdacbo-95j_Rn04XwuW6O1Yw";
		await SetPasskeyUserHandleAsync(user.Id, handle);

		var identityUser = await WithStoreAsync(store => store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
		var identityUserId = await WithStoreAsync(store => store.GetUserIdAsync(identityUser!, CancellationToken.None));

		identityUserId.Should().Be(handle,
			"a user-scoped passkey assertion compares its state to the credential's WebAuthn user handle");
	}

	[Fact]
	public async Task GetPasskeysAsync_honours_a_cancelled_token()
	{
		var user = await CreateUserAsync("passkey.cancel");

		var act = () => WithStoreAsync(store => store.GetPasskeysAsync(user, new(true)));

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	private static UserPasskeyInfo Info(
		byte[] credentialId,
		string name,
		long signCount = 0,
		bool isUserVerified = true,
		bool isBackedUp = false,
		DateTimeOffset? createdAt = null) =>
		new(
			credentialId, [0xAA], createdAt ?? DateTimeOffset.UnixEpoch, (uint)signCount,
			[], isUserVerified, false, isBackedUp,
			[0xBB], [0xCC]) {
			Name = name,
		};

	private async Task<JobTrackIdentityUser> CreateUserAsync(string userName)
	{
		var appUserId = await InsertAppUserAsync(userName);
		return await WithStoreAsync(async store => {
			var user = new JobTrackIdentityUser {
				AppUserId = appUserId,
				UserName = userName,
				NormalizedUserName = userName.ToUpperInvariant(),
				PasswordHash = "initial-hash",
				SecurityStamp = "initial-stamp",
				ConcurrencyStamp = Guid.NewGuid().ToString(),
			};
			_ = await store.CreateAsync(user, CancellationToken.None);
			return user;
		});
	}

	private async Task<T> WithStoreAsync<T>(Func<JobTrackUserStore, Task<T>> action)
	{
		await using var context = CreateContext();
		using var store = new JobTrackUserStore(context, dataProtectionProvider, SystemClock.Instance);
		return await action(store);
	}

	private async Task WithStoreAsync(Func<JobTrackUserStore, Task> action) =>
		await WithStoreAsync(async store => {
			await action(store);
			return true;
		});

	private SqliteJobTrackIdentityDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<SqliteJobTrackIdentityDbContext>()
					  .UseSqlite(database.ConnectionString)
					  .Options;
		return new(options);
	}

	private async Task SetPasskeyUserHandleAsync(long identityUserId, string handle)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE identity_user SET passkey_user_handle = $handle WHERE id = $id;";
		_ = command.Parameters.AddWithValue("$handle", handle);
		_ = command.Parameters.AddWithValue("$id", identityUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task RemovePasskeyAndRotateStampsAsync(long identityUserId, byte[] credentialId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  DELETE FROM identity_user_passkey
							  WHERE identity_user_id = $identityUserId AND credential_id = $credentialId;
							  UPDATE identity_user
							  SET security_stamp = $securityStamp, concurrency_stamp = $concurrencyStamp
							  WHERE id = $identityUserId;
							  """;
		_ = command.Parameters.AddWithValue("$identityUserId", identityUserId);
		_ = command.Parameters.AddWithValue("$credentialId", credentialId);
		_ = command.Parameters.AddWithValue("$securityStamp", Guid.NewGuid().ToString("N"));
		_ = command.Parameters.AddWithValue("$concurrencyStamp", Guid.NewGuid().ToString("N"));
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<AppUserId> InsertAppUserAsync(string displayName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = command.Parameters.AddWithValue("$displayName", displayName);
		var id = (long)(await command.ExecuteScalarAsync())!;
		return new(id);
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
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
