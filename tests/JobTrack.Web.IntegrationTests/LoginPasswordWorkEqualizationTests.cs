namespace JobTrack.Web.IntegrationTests;

using Application;
using AwesomeAssertions;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class LoginPasswordWorkEqualizationTests : IAsyncLifetime, IDisposable
{

	public enum LoginAccountState
	{
		Unknown,
		Disabled,
		Locked,
		WrongPassword,
		WrongPasswordTripsLockout,
		Success,
		TwoFactorRequired,
	}

	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private CountingPasswordHasher passwordHasher = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		passwordHasher = new();
		factory = new(database.ConnectionString, passwordHasher: passwordHasher);
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

	[Theory]
	[InlineData(LoginAccountState.Unknown)]
	[InlineData(LoginAccountState.Disabled)]
	[InlineData(LoginAccountState.Locked)]
	[InlineData(LoginAccountState.WrongPassword)]
	[InlineData(LoginAccountState.WrongPasswordTripsLockout)]
	[InlineData(LoginAccountState.Success)]
	[InlineData(LoginAccountState.TwoFactorRequired)]
	public async Task Every_login_outcome_performs_exactly_one_password_verification(LoginAccountState accountState)
	{
		var userName = $"login-work-{accountState}";
		if (accountState != LoginAccountState.Unknown) {
			await SeedUserAsync(userName, accountState);
		}

		passwordHasher.ResetVerificationCount();
		var suppliedPassword = accountState is LoginAccountState.WrongPassword or LoginAccountState.WrongPasswordTripsLockout
			? "wrong-password"
			: KnownPassword;

		_ = await client.PostLoginAsync(userName, suppliedPassword);

		passwordHasher.VerificationCount.Should().Be(1);
	}

	private async Task SeedUserAsync(string userName, LoginAccountState accountState)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		var appUserId = (long)(await insertAppUser.ExecuteScalarAsync())!;

		var user = CreateUser(appUserId, userName);
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(user, KnownPassword);
		var isEnabled = accountState != LoginAccountState.Disabled;
		var lockoutEnd = accountState == LoginAccountState.Locked
			? (DateTimeOffset.UtcNow.Add(LockoutDuration) - DateTimeOffset.UnixEpoch).Ticks
			: (long?)null;
		var twoFactorEnabled = accountState == LoginAccountState.TwoFactorRequired;
		var accessFailedCount = accountState == LoginAccountState.WrongPasswordTripsLockout
			? AccountLockoutPolicy.MaxFailedAccessAttempts - 1
			: 0;

		await using var insertIdentityUser = connection.CreateCommand();
		insertIdentityUser.CommandText = """
										 INSERT INTO identity_user
										 	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										 	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled,
										 	 lockout_end, access_failed_count, two_factor_enabled, authenticator_key_protected)
										 VALUES
										 	($appUserId, $userName, $normalizedUserName, $passwordHash, $securityStamp,
										 	 $concurrencyStamp, 0, $isEnabled, 1, $lockoutEnd, $accessFailedCount, $twoFactorEnabled, $authenticatorKeyProtected);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", user.NormalizedUserName);
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", user.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", user.ConcurrencyStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$isEnabled", isEnabled);
		_ = insertIdentityUser.Parameters.AddWithValue("$accessFailedCount", accessFailedCount);
		_ = insertIdentityUser.Parameters.AddWithValue("$lockoutEnd", lockoutEnd is not null ? lockoutEnd.Value : DBNull.Value);
		_ = insertIdentityUser.Parameters.AddWithValue("$twoFactorEnabled", twoFactorEnabled);
		_ = insertIdentityUser.Parameters.AddWithValue(
			"$authenticatorKeyProtected",
			twoFactorEnabled
				? new byte[] {
					1,
				}
				: DBNull.Value);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();
	}

	private static JobTrackIdentityUser CreateUser(long appUserId, string userName) => new() {
		AppUserId = new(appUserId),
		UserName = userName,
		NormalizedUserName = userName.ToUpperInvariant(),
		PasswordHash = string.Empty,
		SecurityStamp = Guid.NewGuid().ToString(),
		ConcurrencyStamp = Guid.NewGuid().ToString(),
	};

	private sealed class CountingPasswordHasher : IPasswordHasher<JobTrackIdentityUser>
	{
		private readonly PasswordHasher<JobTrackIdentityUser> inner = new();
		private int verificationCount;

		public int VerificationCount => Volatile.Read(ref verificationCount);

		public string HashPassword(JobTrackIdentityUser user, string password) => inner.HashPassword(user, password);

		public PasswordVerificationResult VerifyHashedPassword(JobTrackIdentityUser user, string hashedPassword, string providedPassword)
		{
			_ = Interlocked.Increment(ref verificationCount);
			return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
		}

		public void ResetVerificationCount() => Interlocked.Exchange(ref verificationCount, 0);
	}
}
