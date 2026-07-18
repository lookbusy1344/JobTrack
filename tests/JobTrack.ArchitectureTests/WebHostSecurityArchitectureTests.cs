namespace JobTrack.ArchitectureTests;

using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Remediation plan §2.12: guardrails for documented web/sample invariants.
/// </summary>
public sealed class WebHostSecurityArchitectureTests
{
	private static readonly string[] AnonymousPageModelAllowlist = [
		"Index",
		"Login",
		"LoginTwoFactor",
		"Logout",
		"AccessDenied",
		"Error",
	];

	private static readonly string[] JobTrackIdentityDbContextAllowlist = [
		"ServiceCollectionExtensions.cs",
		"JobTrackUserStore.cs",
		"EmergencyPasswordReset.cs",
		"Program.cs",
	];

	[Fact]
	public void ExternalApiClient_sample_has_no_JobTrack_library_project_references()
	{
		var projectPath = Path.Combine(
			RepositoryPaths.SolutionRoot(), "samples", "JobTrack.ExternalApiClient", "JobTrack.ExternalApiClient.csproj");
		var document = XDocument.Load(projectPath);
		var references = document.Descendants("ProjectReference")
			.Select(element => Path.GetFileName(element.Attribute("Include")?.Value ?? string.Empty))
			.Where(name => name.StartsWith("JobTrack.", StringComparison.Ordinal))
			.ToList();

		references.Should().BeEmpty("the external API client proof must not reference JobTrack.* assemblies");
	}

	[Fact]
	public void Every_Razor_PageModel_is_authorized_except_the_public_allowlist()
	{
		var pagesDirectory = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "Pages");
		var violations = Directory.EnumerateFiles(pagesDirectory, "*.cshtml.cs", SearchOption.AllDirectories)
			.Select(path => (Content: File.ReadAllText(path),
				ModelName: Path.GetFileName(path).Replace(".cshtml.cs", string.Empty, StringComparison.Ordinal)))
			.Where(entry => !AnonymousPageModelAllowlist.Contains(entry.ModelName, StringComparer.Ordinal))
			.Where(entry => !entry.Content.Contains("[Authorize", StringComparison.Ordinal))
			.Select(entry => entry.ModelName)
			.ToList();

		violations.Should().BeEmpty("workflow pages must declare an authorization policy");
	}

	[Fact]
	public void Every_mapped_api_route_requires_authorization()
	{
		var apiSource = File.ReadAllText(Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "JobTrackApi.cs"));
		var violations = new List<string>();
		var statement = new StringBuilder();
		var depth = 0;
		foreach (var line in apiSource.Split('\n')) {
			if (depth == 0 && line.Contains("api.Map", StringComparison.Ordinal)) {
				statement.Clear();
				statement.AppendLine(line);
				depth += line.Count(c => c == '(') - line.Count(c => c == ')');
				if (depth == 0 && line.Contains(';', StringComparison.Ordinal)) {
					if (!statement.ToString().Contains("RequireAuthorization", StringComparison.Ordinal)) {
						violations.Add(line.Trim());
					}
				}

				continue;
			}

			if (depth > 0) {
				statement.AppendLine(line);
				depth += line.Count(c => c == '(') - line.Count(c => c == ')');
				if (depth <= 0) {
					if (!statement.ToString().Contains("RequireAuthorization", StringComparison.Ordinal)) {
						violations.Add(statement.ToString().Split('\n').First().Trim());
					}

					depth = 0;
				}
			}
		}

		violations.Should().BeEmpty("every /api/* endpoint must call RequireAuthorization");
	}

	[Fact]
	public void JobTrackIdentityDbContext_is_only_used_at_composition_identity_and_allowlisted_pages()
	{
		var violations = new List<string>();
		var srcRoot = Path.Combine(RepositoryPaths.SolutionRoot(), "src");

		foreach (var path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)) {
			if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				|| path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
				continue;
			}

			var content = File.ReadAllText(path);
			if (!content.Contains("JobTrackIdentityDbContext", StringComparison.Ordinal)) {
				continue;
			}

			var fileName = Path.GetFileName(path);
			var relativeDirectory = Path.GetRelativePath(srcRoot, Path.GetDirectoryName(path)!);
			if (relativeDirectory.StartsWith("JobTrack.Identity", StringComparison.Ordinal)
				|| relativeDirectory.StartsWith("JobTrack.AdminCli", StringComparison.Ordinal)
				|| (relativeDirectory.StartsWith("JobTrack.Web", StringComparison.Ordinal)
					&& JobTrackIdentityDbContextAllowlist.Contains(fileName, StringComparer.Ordinal))) {
				continue;
			}

			violations.Add(relativeDirectory + Path.DirectorySeparatorChar + fileName);
		}

		violations.Should().BeEmpty("identity DbContext access must stay in composition, identity, AdminCli, and allowlisted pages");
	}
}
