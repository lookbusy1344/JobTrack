namespace JobTrack.ArchitectureTests;

using Application;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Remediation plan §2.6: mutation authorization lives in persistence ports; Application command
///     handlers must not invoke domain <c>*AccessPolicy</c> types directly.
/// </summary>
public sealed class ApplicationCommandAuthorizationBoundaryTests
{
	private static readonly string[] CommandTypeNames = [
		nameof(InstallationCommands),
		nameof(JobCommands),
		nameof(WorkCommands),
		nameof(ScheduleCommands),
		nameof(RateCommands),
		nameof(EmployeeCommands),
		nameof(RequestCommands),
		nameof(TokenCommands),
	];

	[Fact]
	public void Application_command_handlers_do_not_invoke_access_policies_directly()
	{
		var violations = typeof(IJobTrackClient).Assembly.GetTypes()
			.Where(type => CommandTypeNames.Contains(type.Name, StringComparer.Ordinal))
			.Select(type => (Type: type, Source: File.ReadAllText(FindSourcePath(type))))
			.Where(entry => entry.Source.Contains("AccessPolicy", StringComparison.Ordinal))
			.Select(entry => entry.Type.FullName)
			.ToList();

		violations.Should().BeEmpty(
			"command handlers delegate mutation authorization to persistence ports; query handlers own AccessPolicy checks");
	}

	private static string FindSourcePath(Type type)
	{
		var sourceFile = $"{type.Name}.cs";
		var directory = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Application");
		var path = Directory.EnumerateFiles(directory, sourceFile, SearchOption.AllDirectories).Single();
		return path;
	}
}
