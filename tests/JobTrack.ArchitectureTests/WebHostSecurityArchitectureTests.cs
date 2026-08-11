namespace JobTrack.ArchitectureTests;

using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Remediation plan §2.12: guardrails for documented web/sample invariants.
/// </summary>
public sealed partial class WebHostSecurityArchitectureTests
{
	// Whole repository-relative paths under Pages/, not bare file names: a bare-name allowlist
	// silently exempts any same-named page added elsewhere in the tree later (a new
	// Pages/Admin/Index.cshtml.cs would inherit the landing page's "Index" exemption and ship
	// unauthenticated), which is precisely the regression this guardrail exists to catch.
	private static readonly FrozenSet<string> AnonymousPageAllowlist = FrozenSet.ToFrozenSet([
		"Index.cshtml.cs",
		"Error.cshtml.cs",
		"Account/Login.cshtml.cs",
		"Account/LoginTwoFactor.cshtml.cs",
		"Account/Logout.cshtml.cs",
		"Account/AccessDenied.cshtml.cs",
	], StringComparer.Ordinal);

	// ADR 0066 Stage 6: the complete set of minimal-API routes, mapped directly in Program.cs (not
	// a Razor Page or a JobTrackApi.cs /api/* route -- both covered by the guardrails above), that
	// may call AllowAnonymous(). Liveness/readiness must stay reachable with no session and no
	// credential, but that must not become a silent template for exempting some future route --
	// an entry must be added here deliberately, matching AnonymousPageAllowlist's own shape.
	private static readonly FrozenSet<string> AnonymousMinimalApiRouteAllowlist = FrozenSet.ToFrozenSet([
		"/health/live",
		"/health/ready",
	], StringComparer.Ordinal);

	private static readonly FrozenSet<string> JobTrackIdentityDbContextAllowlist = FrozenSet.ToFrozenSet([
		"ServiceCollectionExtensions.cs",
		"JobTrackUserStore.cs",
		"EmergencyPasswordReset.cs",
		"Program.cs",
	], StringComparer.Ordinal);

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
		var violations = EnumeratePageModels()
			.Where(entry => !IsAllowedAnonymous(entry.RelativePath))
			.Where(entry => !entry.Content.Contains("[Authorize", StringComparison.Ordinal))
			.Select(entry => entry.RelativePath)
			.ToList();

		violations.Should().BeEmpty("workflow pages must declare an authorization policy");
	}

	/// <summary>
	///     The allowlist above is matched against a page's whole path under <c>Pages/</c>. A same-named
	///     page in another folder is a different page and must not inherit the exemption.
	/// </summary>
	[Fact]
	public void The_anonymous_page_allowlist_matches_whole_paths_not_bare_file_names()
	{
		IsAllowedAnonymous("Index.cshtml.cs").Should().BeTrue();
		IsAllowedAnonymous("Account/Login.cshtml.cs").Should().BeTrue();

		IsAllowedAnonymous("Admin/Index.cshtml.cs").Should().BeFalse();
		IsAllowedAnonymous("Jobs/Login.cshtml.cs").Should().BeFalse();
		IsAllowedAnonymous("Account/Nested/Logout.cshtml.cs").Should().BeFalse();
	}

	/// <summary>
	///     A stale allowlist entry is a silent hole: it exempts nothing today, but pre-authorizes a
	///     future page created at that exact path. Every entry must name a page that actually exists.
	/// </summary>
	[Fact]
	public void Every_anonymous_allowlist_entry_names_an_existing_page()
	{
		var actualPaths = EnumeratePageModels().Select(entry => entry.RelativePath).ToList();

		AnonymousPageAllowlist.Should().BeSubsetOf(actualPaths);
	}

	private static bool IsAllowedAnonymous(string relativePath) => AnonymousPageAllowlist.Contains(relativePath);

	private static IEnumerable<(string RelativePath, string Content)> EnumeratePageModels()
	{
		var pagesDirectory = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "Pages");

		return Directory.EnumerateFiles(pagesDirectory, "*.cshtml.cs", SearchOption.AllDirectories)
			.Select(path => (
				RelativePath: Path.GetRelativePath(pagesDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
				Content: File.ReadAllText(path)));
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

	/// <summary>
	///     Mirrors <see cref="Every_mapped_api_route_requires_authorization" />'s statement-scan shape,
	///     but over Program.cs's minimal-API routes instead of JobTrackApi.cs's /api/* group: every
	///     <c>Map*(...)</c> call whose statement calls <c>AllowAnonymous()</c> must name a route on the
	///     allowlist above, so a future anonymous route added to the composition root cannot ship
	///     silently.
	/// </summary>
	[Fact]
	public void Every_anonymous_minimal_API_route_in_Program_is_on_the_health_allowlist()
	{
		var programSource = File.ReadAllText(Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "Program.cs"));
		var violations = new List<string>();
		var statement = new StringBuilder();
		var depth = 0;
		foreach (var line in programSource.Split('\n')) {
			if (depth == 0 && MinimalApiMapCallPattern().IsMatch(line)) {
				statement.Clear();
				_ = statement.AppendLine(line);
				depth += line.Count(c => c == '(') - line.Count(c => c == ')');
				if (depth == 0 && line.Contains(';', StringComparison.Ordinal)) {
					CollectAnonymousRouteViolation(statement.ToString(), violations);
				}

				continue;
			}

			if (depth > 0) {
				_ = statement.AppendLine(line);
				depth += line.Count(c => c == '(') - line.Count(c => c == ')');
				if (depth <= 0) {
					CollectAnonymousRouteViolation(statement.ToString(), violations);
					depth = 0;
				}
			}
		}

		violations.Should().BeEmpty("only the documented health endpoints may be anonymous minimal API routes");
	}

	/// <summary>
	///     The mirror of <see cref="Every_anonymous_allowlist_entry_names_an_existing_page" />: a
	///     health-route allowlist entry that no longer appears in Program.cs is a stale exemption
	///     rather than evidence the endpoint exists.
	/// </summary>
	[Fact]
	public void Every_health_allowlist_entry_is_mapped_in_Program()
	{
		var programSource = File.ReadAllText(Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "Program.cs"));

		foreach (var route in AnonymousMinimalApiRouteAllowlist) {
			programSource.Should().Contain($"\"{route}\"", $"the allowlist names '{route}', which must actually be mapped");
		}
	}

	private static void CollectAnonymousRouteViolation(string statement, List<string> violations)
	{
		if (!statement.Contains("AllowAnonymous()", StringComparison.Ordinal)) {
			return;
		}

		var routeMatch = MapCallRoutePattern().Match(statement);
		var route = routeMatch.Success ? routeMatch.Groups[1].Value : "<unrecognised route>";
		if (!AnonymousMinimalApiRouteAllowlist.Contains(route)) {
			violations.Add(route);
		}
	}

	[GeneratedRegex(@"\.Map(Get|Post|Put|Delete)\(")]
	private static partial Regex MinimalApiMapCallPattern();

	[GeneratedRegex("\"(/[^\"]*)\"")]
	private static partial Regex MapCallRoutePattern();

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
					&& JobTrackIdentityDbContextAllowlist.Contains(fileName))) {
				continue;
			}

			violations.Add(relativeDirectory + Path.DirectorySeparatorChar + fileName);
		}

		violations.Should().BeEmpty("identity DbContext access must stay in composition, identity, AdminCli, and allowlisted pages");
	}
}
