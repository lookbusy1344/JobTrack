namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Cross-browser compatibility evidence (plan §8.7): sign-in and job-tree browsing -- the
///     representative workflow already proven at the full viewport matrix under Chromium
///     (<see cref="JobBrowseBrowserTestsBase" />) -- also renders and functions under Firefox and
///     WebKit. Chromium already carries the full functional/provider/accessibility matrix; this adds
///     one smoke pass per additional engine, not a repeat of that whole matrix, since rendering-engine
///     differences are what's under test here, not workflow correctness (already proven).
/// </summary>
/// <remarks>
///     Requires <c>playwright install firefox webkit</c> (in addition to <c>chromium</c>) to have been
///     run once outside this repo's usual <c>dotnet restore</c>/<c>dotnet build</c> -- see
///     <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class CrossBrowserCompatibilityTestsBase
{
	private const int DesktopWidth = 1280;
	private const int DesktopHeight = 800;

	private readonly BrowserFixture fixture;

	protected CrossBrowserCompatibilityTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	[Fact]
	public async Task Signing_in_and_browsing_the_job_tree_works()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		(await page.Locator("text=Root").First.IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task The_job_browse_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var results = await page.RunAxe();
		var criticalOrSerious = results.Violations.Where(violation => violation.Impact is "critical" or "serious").ToArray();

		criticalOrSerious.Should().BeEmpty(
			"/Jobs/Browse should have no critical/serious accessibility violations, found: " +
			string.Join("; ", criticalOrSerious.Select(v => $"{v.Id} ({v.Impact}): {v.Help}")));
	}

	private async Task SignInAsync(IPage page)
	{
		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		await page.Locator("#Input_UserName").FillAsync(BrowserFixture.AdministratorUserName);
		await page.Locator("#Input_Password").FillAsync(BrowserFixture.AdministratorPassword);
		await page.Locator("button[type=submit]").ClickAsync();
		await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
	}
}

public sealed class FirefoxCrossBrowserCompatibilityTests : CrossBrowserCompatibilityTestsBase, IClassFixture<FirefoxBrowserFixture>
{
	public FirefoxCrossBrowserCompatibilityTests(FirefoxBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class WebKitCrossBrowserCompatibilityTests : CrossBrowserCompatibilityTestsBase, IClassFixture<WebKitBrowserFixture>
{
	public WebKitCrossBrowserCompatibilityTests(WebKitBrowserFixture fixture) : base(fixture)
	{
	}
}
