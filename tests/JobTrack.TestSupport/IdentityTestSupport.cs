namespace JobTrack.TestSupport;

using Database;
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
