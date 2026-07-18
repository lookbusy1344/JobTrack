namespace JobTrack.TestSupport;

using Database;

/// <summary>
///     Locates repository-relative paths from a test's runtime working
///     directory by walking up to the solution file, so tests do not depend on
///     the test runner's current directory.
/// </summary>
public static class RepositoryPaths
{
	public static string SolutionRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JobTrack.slnx"))) {
			directory = directory.Parent;
		}

		return directory?.FullName
			   ?? throw new InvalidOperationException("Could not locate JobTrack.slnx above " + AppContext.BaseDirectory);
	}

	public static string SchemaVersionsDirectory(SchemaProvider provider)
	{
		var providerDirectoryName = provider switch {
			SchemaProvider.PostgreSql => "postgresql",
			SchemaProvider.Sqlite => "sqlite",
			_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
		};

		return Path.Combine(SolutionRoot(), "database", providerDirectoryName, "schema-versions");
	}

	/// <summary>
	///     PostgreSQL-only: the fixed, unversioned roles-and-grants script
	///     (impl plan §6.1, §6.7) applied after schema deployment, not tracked
	///     in <c>schema_version</c>.
	/// </summary>
	public static string PostgreSqlRolesAndGrantsScriptPath() =>
		Path.Combine(SolutionRoot(), "database", "postgresql", "roles", "jobtrack-roles-and-grants.sql");
}
