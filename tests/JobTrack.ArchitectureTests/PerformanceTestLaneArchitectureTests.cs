namespace JobTrack.ArchitectureTests;

using System.Text.Json;
using AwesomeAssertions;
using TestSupport;

public sealed class PerformanceTestLaneArchitectureTests
{
	private const string CleanupTrap = "trap cleanup_test_databases EXIT";

	[Fact]
	public void Performance_test_project_serializes_test_collections()
	{
		var configurationPath = Path.Combine(
			RepositoryPaths.SolutionRoot(),
			"tests",
			"JobTrack.Database.PerformanceTests",
			"xunit.runner.json");
		using var configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));

		configuration.RootElement.GetProperty("parallelizeTestCollections").GetBoolean().Should().BeFalse(
			"latency ceilings require every performance test class to run without intra-project PostgreSQL contention");
	}

	[Theory]
	[InlineData("perf-test.sh")]
	[InlineData("all-test.sh")]
	public void Database_test_runner_registers_cleanup_before_running_tests(string scriptName)
	{
		var scriptPath = Path.Combine(RepositoryPaths.SolutionRoot(), "scripts", scriptName);
		var script = File.ReadAllText(scriptPath);

		var trapPosition = script.IndexOf(CleanupTrap, StringComparison.Ordinal);
		var testPosition = script.IndexOf("gtimeout ", StringComparison.Ordinal);

		trapPosition.Should().BeGreaterThanOrEqualTo(0, $"{scriptName} must clean databases on every exit path");
		trapPosition.Should().BeLessThan(testPosition, $"{scriptName} must register cleanup before a test can fail or time out");
	}
}
