namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;

public static class DatabaseContractTestSupport
{
	public static async Task<DbConnection> OpenExistingConnectionAsync(
		this IDisposableTestDatabase database,
		Func<string, DbConnection> createConnection,
		Func<DbConnection, Task> prepareConnectionAsync)
	{
		var connection = createConnection(database.ConnectionString);
		await connection.OpenAsync();
		await prepareConnectionAsync(connection);
		return connection;
	}

	public static void AddParameter(this DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		_ = command.Parameters.Add(parameter);
	}

	public static async Task AssignRoleAsync(DbConnection connection, AppUserId appUserId, EmployeeRole role)
	{
		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		roleCommand.AddParameter("@appUserId", appUserId.Value);
		roleCommand.AddParameter("@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();
	}

	public static async Task<AppUserId> SeedEmployeeAsync(
		IDisposableTestDatabase database,
		Func<string, DbConnection> createConnection,
		Func<DbConnection, Task> prepareConnectionAsync,
		string displayName,
		string userName,
		EmployeeRole role)
	{
		await using var connection = await database.OpenExistingConnectionAsync(createConnection, prepareConnectionAsync);

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		appUserCommand.AddParameter("@displayName", displayName);
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
		identityUserCommand.AddParameter("@appUserId", appUserId.Value);
		identityUserCommand.AddParameter("@userName", userName);
		identityUserCommand.AddParameter("@normalizedUserName", userName.ToUpperInvariant());
		identityUserCommand.AddParameter("@securityStamp", Guid.NewGuid().ToString("N"));
		identityUserCommand.AddParameter("@concurrencyStamp", Guid.NewGuid().ToString("N"));
		identityUserCommand.AddParameter("@requiresPasswordChange", false);
		identityUserCommand.AddParameter("@isEnabled", true);
		identityUserCommand.AddParameter("@lockoutEnabled", true);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await AssignRoleAsync(connection, appUserId, role);

		return appUserId;
	}
}
