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
	private const int ReflowWidth = 320;
	private const int ReflowHeight = 640;

	private readonly BrowserFixture fixture;

	protected PersonalAccessTokensBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	/// <summary>
	///     Scanned with a token issued, not on the empty page: issuing is what renders the token table and
	///     the one-time-secret warning alert, and the empty page has neither. The warning alert in
	///     particular was the app's only <c>.alert-warning</c> and had no Console skin, so nothing had ever
	///     contrast-checked the colours it actually rendered in.
	/// </summary>
	[Fact]
	public async Task The_personal_access_tokens_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await IssueTokenAsync(page, "Nightly export job");

		(await page.Locator(".alert-warning").IsVisibleAsync()).Should().BeTrue(
			"the scan is only meaningful if the one-time secret is on screen");

		var results = await page.RunAxe();
		var criticalOrSerious = results.Violations.Where(violation => violation.Impact is "critical" or "serious").ToArray();

		criticalOrSerious.Should().BeEmpty(
			"/Account/PersonalAccessTokens should have no critical/serious accessibility violations, found: " +
			string.Join("; ", criticalOrSerious.Select(v => $"{v.Id} ({v.Impact}): {v.Help}")));
	}

	/// <summary>
	///     WCAG 1.4.10 Reflow at the standard 320 CSS px test width, with the token table actually
	///     populated: an empty table reflows trivially, so only a real row exercises the six columns'
	///     combined minimum width. This page previously carried desktop-only browser coverage, and its
	///     table dropped no columns at any breakpoint — 420px of table in a 320px viewport.
	/// </summary>
	[Fact]
	public async Task Reflowing_the_token_page_to_a_320px_wide_viewport_keeps_the_table_within_the_page()
	{
		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await IssueTokenAsync(page, "Nightly export job");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth,
			"WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		// What the row is, what state it is in, and its one action survive the column drops.
		(await page.GetByText("Nightly export job", new() { Exact = false }).First.IsVisibleAsync()).Should().BeTrue();
		(await page.GetByText("Active", new() { Exact = true }).First.IsVisibleAsync()).Should().BeTrue();
		// .First: the axe test in this class issues a token too, and both share the fixture's database.
		(await page.Locator("button[title='Revoke token']").First.IsVisibleAsync()).Should().BeTrue();
	}

	private async Task IssueTokenAsync(IPage page, string label)
	{
		await page.GotoAsync($"{fixture.BaseAddress}/Account/PersonalAccessTokens");
		await page.Locator("#Issue_Label").FillAsync(label);
		// Scoped to the issue form: at 320px the header's own submit ("Sign out") collapses behind the
		// toggler, so an unscoped `button[type=submit]` resolves to a hidden element.
		await page.Locator("form:has(#Issue_Label) button[type=submit]").ClickAsync();
		// The handler redirects (PRG); without waiting, a measurement here reads the pre-submit document.
		await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
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
