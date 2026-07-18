namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser accessibility evidence for the self-service personal access token page (security
///     review remediation §2.2), matching every other Console-language page's axe gate.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class PersonalAccessTokensBrowserTestsBase
{
	private const int DesktopWidth = 1280;
	private const int DesktopHeight = 800;

	private readonly BrowserFixture fixture;

	protected PersonalAccessTokensBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	[Fact]
	public async Task The_personal_access_tokens_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Account/PersonalAccessTokens");

		var results = await page.RunAxe();
		var criticalOrSerious = results.Violations.Where(violation => violation.Impact is "critical" or "serious").ToArray();

		criticalOrSerious.Should().BeEmpty(
			"/Account/PersonalAccessTokens should have no critical/serious accessibility violations, found: " +
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

public sealed class SqlitePersonalAccessTokensBrowserTests : PersonalAccessTokensBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqlitePersonalAccessTokensBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlPersonalAccessTokensBrowserTests : PersonalAccessTokensBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlPersonalAccessTokensBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
