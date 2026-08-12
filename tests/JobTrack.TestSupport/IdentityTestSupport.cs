namespace JobTrack.TestSupport;

using Abstractions;
using Database;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Npgsql;

/// <summary>
///     Direct identity-store adjustments for integration, browser, and end-to-end fixtures. Bootstrap and
///     employee creation always leave accounts with <c>requires_password_change</c> set (spec §8.1); suites
///     that need a PAT or signed-in session to reach protected routes clear the flag here rather than
///     re-proving the redirect.
/// </summary>
public static class IdentityTestSupport
{
	public static async Task<AppUserId> SeedSqliteEmployeeAsync(
		string connectionString,
		string password,
		string userName,
		EmployeeRole role = EmployeeRole.Worker,
		string ianaTimeZone = "UTC")
	{
		await using var connection = new SqliteConnection(connectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, $ianaTimeZone); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		_ = insertAppUser.Parameters.AddWithValue("$ianaTimeZone", ianaTimeZone);
		var appUserId = (long)(await insertAppUser.ExecuteScalarAsync())!;

		var identityUser = new JobTrackIdentityUser {
			AppUserId = new(appUserId),
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = string.Empty,
			SecurityStamp = Guid.NewGuid().ToString(),
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(identityUser, password);

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
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", identityUser.NormalizedUserName);
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", identityUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", identityUser.ConcurrencyStamp);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();

		await using var insertRole = connection.CreateCommand();
		insertRole.CommandText =
			"INSERT INTO identity_user_role (identity_user_id, identity_role_id) SELECT id, $roleId FROM identity_user WHERE app_user_id = $appUserId;";
		_ = insertRole.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)role);
		_ = await insertRole.ExecuteNonQueryAsync();

		return new(appUserId);
	}

	public static async Task ClearRequiresPasswordChangeAsync(SchemaProvider provider, string connectionString)
	{
		switch (provider) {
			case SchemaProvider.Sqlite:
				await using (var connection = new SqliteConnection(connectionString)) {
					await connection.OpenAsync();
					await using var command = connection.CreateCommand();
					command.CommandText = "UPDATE identity_user SET requires_password_change = 0;";
					_ = await command.ExecuteNonQueryAsync();
				}

				break;
			case SchemaProvider.PostgreSql:
				await using (var connection = new NpgsqlConnection(connectionString)) {
					await connection.OpenAsync();
					await using var command = connection.CreateCommand();
					command.CommandText = "UPDATE identity_user SET requires_password_change = false;";
					_ = await command.ExecuteNonQueryAsync();
				}

				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.");
		}
	}
}
