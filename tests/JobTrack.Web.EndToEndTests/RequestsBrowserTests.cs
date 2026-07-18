namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser accessibility evidence for the requester self-service pages (ADR 0033/0034, plan
///     §8 <c>/Requests</c> and <c>/Requests/{id}</c>), matching every other Console-language page's axe
///     gate.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class RequestsBrowserTestsBase
{
	private const int DesktopWidth = 1280;
	private const int DesktopHeight = 800;
	private const string RequesterUserName = "rita.browser.e2e";
	private const string RequesterPassword = "Requester-Horse-Battery-42!";

	private readonly BrowserFixture fixture;

	protected RequestsBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	[Fact]
	public async Task The_requests_list_page_has_no_critical_or_serious_accessibility_violations()
	{
		_ = await fixture.SeedRequesterAsync(RequesterUserName, RequesterPassword);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page, RequesterUserName, RequesterPassword);
		await page.GotoAsync($"{fixture.BaseAddress}/Requests");

		var results = await page.RunAxe();
		var criticalOrSerious = results.Violations.Where(violation => violation.Impact is "critical" or "serious").ToArray();

		criticalOrSerious.Should().BeEmpty(
			"/Requests should have no critical/serious accessibility violations, found: " +
			string.Join("; ", criticalOrSerious.Select(v => $"{v.Id} ({v.Impact}): {v.Help}")));
	}

	[Fact]
	public async Task The_request_detail_page_has_no_critical_or_serious_accessibility_violations()
	{
		var requesterId = await fixture.SeedRequesterAsync("rita.detail.browser.e2e", RequesterPassword);
		var holdingAreaId = await fixture.SeedHoldingAreaAsync();
		var submitted = await fixture.SubmitRequestAsync(requesterId, holdingAreaId, "Printer will not turn on");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page, "rita.detail.browser.e2e", RequesterPassword);
		await page.GotoAsync($"{fixture.BaseAddress}/Requests/{submitted.JobNodeId.Value}");

		var results = await page.RunAxe();
		var criticalOrSerious = results.Violations.Where(violation => violation.Impact is "critical" or "serious").ToArray();

		criticalOrSerious.Should().BeEmpty(
			"/Requests/{id} should have no critical/serious accessibility violations, found: " +
			string.Join("; ", criticalOrSerious.Select(v => $"{v.Id} ({v.Impact}): {v.Help}")));
	}

	private async Task SignInAsync(IPage page, string userName, string password)
	{
		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		await page.Locator("#Input_UserName").FillAsync(userName);
		await page.Locator("#Input_Password").FillAsync(password);
		await page.Locator("button[type=submit]").ClickAsync();
		await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
	}
}

public sealed class SqliteRequestsBrowserTests : RequestsBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteRequestsBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlRequestsBrowserTests : RequestsBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlRequestsBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
