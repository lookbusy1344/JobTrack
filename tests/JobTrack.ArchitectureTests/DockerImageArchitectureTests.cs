namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using TestSupport;

public sealed class DockerImageArchitectureTests
{
	[Fact]
	public void Demo_image_seeds_the_published_requester_account_and_its_request_scenario()
	{
		var dockerfile = File.ReadAllText(Path.Combine(RepositoryPaths.SolutionRoot(), "Dockerfile"));

		dockerfile.Should().Contain("ARG REQUESTER_USERNAME=requester");
		dockerfile.Should().Contain("ARG REQUESTER_PASSWORD=requester1234");
		dockerfile.Should().Contain("--roles Requester --no-force-password-change");
		dockerfile.Should().Contain("/app/uatseed/JobTrack.UatSeed --provider sqlite");
		dockerfile.Should().Contain(
			"--requester-demo --requester-username \"$REQUESTER_USERNAME\" --job-manager-username \"$DEMO_USERNAME\"");
	}
}
