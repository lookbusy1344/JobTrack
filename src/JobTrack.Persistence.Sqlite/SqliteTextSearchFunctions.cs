namespace JobTrack.Persistence.Sqlite;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     Registers and maps SQLite text-search operations whose semantics must match the shared .NET
///     contract rather than SQLite's ASCII-only <c>lower()</c>.
/// </summary>
internal static class SqliteTextSearchFunctions
{
	private const string OrdinalIgnoreCaseContainsFunctionName = "ordinal_ignore_case_contains";

	public static void Configure(ModelBuilder modelBuilder)
	{
		var method = typeof(SqliteTextSearchFunctions).GetMethod(nameof(ContainsOrdinalIgnoreCase))
					 ?? throw new InvalidOperationException("The SQLite ordinal-ignore-case search method is missing.");
		_ = modelBuilder.HasDbFunction(method).HasName(OrdinalIgnoreCaseContainsFunctionName);
	}

	public static bool ContainsOrdinalIgnoreCase(string source, string value) =>
		throw new InvalidOperationException("This method is translated to a SQLite function and must not execute in the CLR.");

	public static void Register(SqliteConnection connection) =>
		connection.CreateFunction<string, string, bool>(
			OrdinalIgnoreCaseContainsFunctionName,
			(source, value) => source.Contains(value, StringComparison.OrdinalIgnoreCase),
			true);
}
