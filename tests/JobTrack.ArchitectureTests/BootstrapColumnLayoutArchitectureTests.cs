namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Keeps responsive multi-column page layout in Bootstrap's twelve-column system rather than
///     introducing a second grid definition in the Console visual stylesheet.
/// </summary>
public sealed class BootstrapColumnLayoutArchitectureTests
{
	[Fact]
	public void Hand_written_css_does_not_define_page_column_grids()
	{
		var stylesheet = File.ReadAllText(Path.Combine(
			RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "wwwroot", "css", "site.css"));

		stylesheet.Should().NotContain("grid-template-columns",
			"multi-column page layout belongs in Bootstrap row and col-* classes in Razor markup");
	}
}
