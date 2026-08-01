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
	private const int SmallPhoneWidth = 375;
	private const int SmallPhoneHeight = 667;
	private const int ReflowWidth = 320;
	private const int ReflowHeight = 640;
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

	/// <summary>
	///     The record card's label column, on a phone. Every field's label used to be a fixed 25% of the
	///     card with <c>white-space: nowrap</c>, so a label wider than that overran its box and printed
	///     across its own value -- which is exactly what "ACKNOWLEDGED" did over its timestamp. The label
	///     is a real grid column now (<c>col-12 col-sm-4</c>), stacked above its value below <c>sm</c>, so
	///     the guarantee to assert is geometric and layout-level rather than a copy length: no label's box
	///     may overlap its own value's box, whatever the label says.
	/// </summary>
	[Theory]
	[InlineData(ReflowWidth, ReflowHeight)]
	[InlineData(SmallPhoneWidth, SmallPhoneHeight)]
	[InlineData(DesktopWidth, DesktopHeight)]
	public async Task No_record_card_label_overlaps_its_own_value(int width, int height)
	{
		var userName = $"rita.labels.{width}.e2e";
		var requesterId = await fixture.SeedRequesterAsync(userName, RequesterPassword);
		var holdingAreaId = await fixture.SeedHoldingAreaAsync();
		var submitted = await fixture.SubmitRequestAsync(requesterId, holdingAreaId, "Printer will not turn on");

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page, userName, RequesterPassword);
		await page.GotoAsync($"{fixture.BaseAddress}/Requests/{submitted.JobNodeId.Value}");

		// Two ways a label can collide with its value, and the original defect was the second: the label's
		// *box* stayed at its fixed 25% while its unwrappable text painted straight out of it. So check the
		// text against its own box as well as the two boxes against each other.
		var overlaps = await page.EvaluateAsync<string[]>(
			"""
			() => {
				const bad = [];
				for (const field of document.querySelectorAll('dl > div')) {
					const dt = field.querySelector('dt'), dd = field.querySelector('dd');
					if (!dt || !dd) continue;
					const a = dt.getBoundingClientRect(), b = dd.getBoundingClientRect();
					if (a.right > b.left + 0.5 && a.left < b.right - 0.5
						&& a.bottom > b.top + 0.5 && a.top < b.bottom - 0.5) {
						bad.push(`box: ${dt.textContent.trim()} over ${dd.textContent.trim().slice(0, 24)}`);
					}
					if (dt.scrollWidth > dt.clientWidth + 0.5) {
						bad.push(`text: "${dt.textContent.trim()}" overruns its own box (${dt.scrollWidth} > ${dt.clientWidth})`);
					}
				}
				return bad;
			}
			""");

		overlaps.Should().BeEmpty("a field's label must never be painted over its own value");

		(await page.Locator("dl dt").CountAsync()).Should().BeGreaterThan(0, "the card must actually have rendered its fields");
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
