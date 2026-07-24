namespace JobTrack.Identity.Tests;

using System.Globalization;
using System.Text;
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
///     Exercises <see cref="JobTrackUserStore" /> against a real, schema-deployed SQLite database
///     (§8.5 slice 1 scope: identity, password, security stamp, lockout, TOTP two-factor -- ADR 0022,
///     ADR 0037). Each assertion re-opens a fresh <see cref="SqliteJobTrackIdentityDbContext" /> to
///     prove the round trip went through persistence, not just in-memory object state.
/// </summary>
public sealed class JobTrackUserStoreTests : IAsyncLifetime
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
	public async Task Creating_a_user_persists_it_for_lookup_by_normalized_name()
	{
		var appUserId = await InsertAppUserAsync("Ada Lovelace");

		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "ada"));

		user.Id.Should().BePositive();
		var reloaded = await WithStoreAsync(store => store.FindByNameAsync("ADA", CancellationToken.None));
		reloaded.Should().NotBeNull();
		reloaded!.UserName.Should().Be("ada");
		reloaded.AppUserId.Should().Be(appUserId);
	}

	[Fact]
	public async Task Password_hash_round_trips_through_persistence()
	{
		var appUserId = await InsertAppUserAsync("Grace Hopper");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "grace"));

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.SetPasswordHashAsync(loaded!, "new-hash", CancellationToken.None);
			_ = await store.UpdateAsync(loaded!, CancellationToken.None);
			return loaded;
		});

		var reloadedHash = await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("GRACE", CancellationToken.None);
			return await store.GetPasswordHashAsync(loaded!, CancellationToken.None);
		});
		reloadedHash.Should().Be("new-hash");
	}

	[Fact]
	public async Task Security_stamp_round_trips_through_persistence()
	{
		var appUserId = await InsertAppUserAsync("Katherine Johnson");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "katherine"));

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.SetSecurityStampAsync(loaded!, "rotated-stamp", CancellationToken.None);
			_ = await store.UpdateAsync(loaded!, CancellationToken.None);
			return loaded;
		});

		var stamp = await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("KATHERINE", CancellationToken.None);
			return await store.GetSecurityStampAsync(loaded!, CancellationToken.None);
		});
		stamp.Should().Be("rotated-stamp");
	}

	[Fact]
	public async Task Lockout_end_and_access_failed_count_round_trip_through_persistence()
	{
		var appUserId = await InsertAppUserAsync("Margaret Hamilton");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "margaret"));
		var lockoutEnd = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			_ = await store.IncrementAccessFailedCountAsync(loaded!, CancellationToken.None);
			_ = await store.IncrementAccessFailedCountAsync(loaded!, CancellationToken.None);
			await store.SetLockoutEndDateAsync(loaded!, lockoutEnd, CancellationToken.None);
			_ = await store.UpdateAsync(loaded!, CancellationToken.None);
			return loaded;
		});

		var (failedCount, reloadedLockoutEnd) = await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("MARGARET", CancellationToken.None);
			var count = await store.GetAccessFailedCountAsync(loaded!, CancellationToken.None);
			var end = await store.GetLockoutEndDateAsync(loaded!, CancellationToken.None);
			return (count, end);
		});

		failedCount.Should().Be(2);
		reloadedLockoutEnd.Should().Be(lockoutEnd);
	}

	[Fact]
	public async Task Authenticator_key_round_trips_through_persistence_encrypted_at_rest()
	{
		var appUserId = await InsertAppUserAsync("Ada Enrolling");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "ada.enrolling"));
		const string authenticatorKey = "JBSWY3DPEHPK3PXP";

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.SetAuthenticatorKeyAsync(loaded!, authenticatorKey, CancellationToken.None);
			await store.SetTwoFactorEnabledAsync(loaded!, true, CancellationToken.None);
			_ = await store.UpdateAsync(loaded!, CancellationToken.None);
			return loaded;
		});

		var (reloadedKey, twoFactorEnabled) = await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("ADA.ENROLLING", CancellationToken.None);
			var key = await store.GetAuthenticatorKeyAsync(loaded!, CancellationToken.None);
			var enabled = await store.GetTwoFactorEnabledAsync(loaded!, CancellationToken.None);
			return (key, enabled);
		});

		reloadedKey.Should().Be(authenticatorKey);
		twoFactorEnabled.Should().BeTrue();

		var rawColumnValue = await ReadAuthenticatorKeyColumnAsync(user.Id);
		rawColumnValue.Should().NotBeNull();
		Encoding.UTF8.GetString(rawColumnValue!).Should().NotContain(authenticatorKey);
	}

	[Fact]
	public async Task Disabling_two_factor_clears_the_enabled_timestamp()
	{
		var appUserId = await InsertAppUserAsync("Grace Disabling");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "grace.disabling"));

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.SetAuthenticatorKeyAsync(loaded!, "JBSWY3DPEHPK3PXP", CancellationToken.None);
			await store.SetTwoFactorEnabledAsync(loaded!, true, CancellationToken.None);
			_ = await store.UpdateAsync(loaded!, CancellationToken.None);
			return loaded;
		});

		await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("GRACE.DISABLING", CancellationToken.None);
			await store.SetTwoFactorEnabledAsync(loaded!, false, CancellationToken.None);
			_ = await store.UpdateAsync(loaded!, CancellationToken.None);
			return loaded;
		});

		var enabledAt = await ReadTwoFactorEnabledAtAsync(user.Id);
		enabledAt.Should().BeNull();
	}

	[Fact]
	public async Task Updating_from_stale_data_throws_dbupdateconcurrencyexception()
	{
		var appUserId = await InsertAppUserAsync("Concurrent Writer");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "concurrent.writer"));

		await using var contextA = CreateContext();
		using var storeA = new JobTrackUserStore(contextA, dataProtectionProvider, SystemClock.Instance);
		var userA = await storeA.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);

		await using var contextB = CreateContext();
		using var storeB = new JobTrackUserStore(contextB, dataProtectionProvider, SystemClock.Instance);
		var userB = await storeB.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);

		await storeA.SetSecurityStampAsync(userA!, "stamp-from-a", CancellationToken.None);
		_ = await storeA.UpdateAsync(userA!, CancellationToken.None);

		await storeB.SetSecurityStampAsync(userB!, "stamp-from-b", CancellationToken.None);
		var act = () => storeB.UpdateAsync(userB!, CancellationToken.None);

		await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
	}

	[Fact]
	public async Task Reload_after_a_lost_concurrency_race_recovers_the_winners_value_not_the_losers_stale_one()
	{
		var appUserId = await InsertAppUserAsync("Reload After Race");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "reload.after.race"));

		await using var contextA = CreateContext();
		using var storeA = new JobTrackUserStore(contextA, dataProtectionProvider, SystemClock.Instance);
		var userA = await storeA.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);

		await using var contextB = CreateContext();
		using var storeB = new JobTrackUserStore(contextB, dataProtectionProvider, SystemClock.Instance);
		var userB = await storeB.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);

		await storeA.SetAuthenticatorKeyAsync(userA!, "winners-key", CancellationToken.None);
		_ = await storeA.UpdateAsync(userA!, CancellationToken.None);

		await storeB.SetAuthenticatorKeyAsync(userB!, "losers-key", CancellationToken.None);
		var act = () => storeB.UpdateAsync(userB!, CancellationToken.None);
		await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

		// Re-querying by id here would be a trap: EF Core's identity map would hand back this same
		// tracked-but-never-persisted instance, still holding "losers-key" locally. ReloadAsync must
		// overwrite it with what actually made it to the database.
		await storeB.ReloadAsync(userB!, CancellationToken.None);
		var recoveredKey = await storeB.GetAuthenticatorKeyAsync(userB!, CancellationToken.None);

		recoveredKey.Should().Be("winners-key");
	}

	[Fact]
	public async Task Assigning_a_role_makes_it_visible_through_get_roles_and_is_in_role()
	{
		var appUserId = await InsertAppUserAsync("Ada Lovelace (roles)");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "ada.roles"));

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.AddToRoleAsync(loaded!, "Administrator", CancellationToken.None);
			return loaded;
		});

		var (roles, isInRole, isInOtherRole) = await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("ADA.ROLES", CancellationToken.None);
			var roles = await store.GetRolesAsync(loaded!, CancellationToken.None);
			var isInRole = await store.IsInRoleAsync(loaded!, "Administrator", CancellationToken.None);
			var isInOtherRole = await store.IsInRoleAsync(loaded!, "Worker", CancellationToken.None);
			return (roles, isInRole, isInOtherRole);
		});

		roles.Should().ContainSingle().Which.Should().Be("Administrator");
		isInRole.Should().BeTrue();
		isInOtherRole.Should().BeFalse();
	}

	[Fact]
	public async Task Assigning_an_already_held_role_is_idempotent()
	{
		var appUserId = await InsertAppUserAsync("Grace Hopper (roles)");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "grace.roles"));

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.AddToRoleAsync(loaded!, "Worker", CancellationToken.None);
			await store.AddToRoleAsync(loaded!, "Worker", CancellationToken.None);
			return loaded;
		});

		var roles = await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("GRACE.ROLES", CancellationToken.None);
			return await store.GetRolesAsync(loaded!, CancellationToken.None);
		});

		roles.Should().ContainSingle().Which.Should().Be("Worker");
	}

	[Fact]
	public async Task Removing_a_role_removes_it_from_get_roles()
	{
		var appUserId = await InsertAppUserAsync("Katherine Johnson (roles)");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "katherine.roles"));

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.AddToRoleAsync(loaded!, "Auditor", CancellationToken.None);
			await store.RemoveFromRoleAsync(loaded!, "Auditor", CancellationToken.None);
			return loaded;
		});

		var roles = await WithStoreAsync(async store => {
			var loaded = await store.FindByNameAsync("KATHERINE.ROLES", CancellationToken.None);
			return await store.GetRolesAsync(loaded!, CancellationToken.None);
		});

		roles.Should().BeEmpty();
	}

	[Fact]
	public async Task Removing_an_unheld_role_is_a_no_op()
	{
		var appUserId = await InsertAppUserAsync("Margaret Hamilton (roles)");
		var user = await WithStoreAsync(store => CreatePersistedUserAsync(store, appUserId, "margaret.roles"));

		var act = () => WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.RemoveFromRoleAsync(loaded!, "Worker", CancellationToken.None);
			return loaded;
		});

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task GetUsersInRoleAsync_returns_only_users_holding_that_role()
	{
		var adminAppUserId = await InsertAppUserAsync("Rosalind Franklin (roles)");
		var admin = await WithStoreAsync(store => CreatePersistedUserAsync(store, adminAppUserId, "rosalind.roles"));
		var workerAppUserId = await InsertAppUserAsync("Grace Hopper (get-users-in-role)");
		_ = await WithStoreAsync(store => CreatePersistedUserAsync(store, workerAppUserId, "grace.get-users-in-role"));

		await WithStoreAsync(async store => {
			var loaded = await store.FindByIdAsync(admin.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
			await store.AddToRoleAsync(loaded!, "Administrator", CancellationToken.None);
			return loaded;
		});

		var administrators = await WithStoreAsync(store => store.GetUsersInRoleAsync("Administrator", CancellationToken.None));

		administrators.Should().ContainSingle().Which.UserName.Should().Be("rosalind.roles");
	}

	private static async Task<JobTrackIdentityUser> CreatePersistedUserAsync(JobTrackUserStore store, AppUserId appUserId, string userName)
	{
		var user = new JobTrackIdentityUser {
			AppUserId = appUserId,
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = "initial-hash",
			SecurityStamp = "initial-stamp",
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var result = await store.CreateAsync(user, CancellationToken.None);
		result.Should().Be(IdentityResult.Success);
		return user;
	}

	private async Task<T> WithStoreAsync<T>(Func<JobTrackUserStore, Task<T>> action)
	{
		await using var context = CreateContext();
		using var store = new JobTrackUserStore(context, dataProtectionProvider, SystemClock.Instance);
		return await action(store);
	}

	private SqliteJobTrackIdentityDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<SqliteJobTrackIdentityDbContext>()
			.UseSqlite(database.ConnectionString)
			.Options;
		return new(options);
	}

	private async Task<byte[]?> ReadAuthenticatorKeyColumnAsync(long identityUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT authenticator_key_protected FROM identity_user WHERE id = $id;";
		_ = command.Parameters.AddWithValue("$id", identityUserId);
		var value = await command.ExecuteScalarAsync();
		return value is DBNull or null ? null : (byte[])value;
	}

	private async Task<long?> ReadTwoFactorEnabledAtAsync(long identityUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT two_factor_enabled_at FROM identity_user WHERE id = $id;";
		_ = command.Parameters.AddWithValue("$id", identityUserId);
		var value = await command.ExecuteScalarAsync();
		return value is DBNull or null ? null : (long)value;
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
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
