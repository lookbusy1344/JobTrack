namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for <c>/Jobs/ConcurrentWork</c>: the concurrent-work table reflows rather
///     than scrolling sideways at every viewport in the matrix, and the page carries no critical or
///     serious accessibility violation.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class ConcurrentWorkBrowserTestsBase
{
	// The same representative viewport matrix as CostReportBrowserTests: small phone, large phone,
	// tablet, and desktop.
	private const int SmallPhoneWidth = 375;
	private const int SmallPhoneHeight = 667;
	private const int LargePhoneWidth = 414;
	private const int LargePhoneHeight = 896;
	private const int TabletWidth = 768;
	private const int TabletHeight = 1024;
	private const int DesktopWidth = 1280;
	private const int DesktopHeight = 800;
	private const int ReflowWidth = 320;
	private const int ReflowHeight = 640;

	private readonly BrowserFixture fixture;

	protected ConcurrentWorkBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_concurrent_work_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		var (leafId, _, _) = await fixture.SeedFinishedSessionAsync($"Overflow concurrent work leaf {width}x{height}");

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await BrowserTestSupport.SignInAdministratorAsync(page, fixture.BaseAddress);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/ConcurrentWork?nodeId={leafId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"the concurrent work page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_the_concurrent_work_page_to_a_320px_wide_viewport_keeps_the_report_readable()
	{
		var (leafId, _, _) = await fixture.SeedFinishedSessionAsync("Reflow concurrent work leaf");

		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await BrowserTestSupport.SignInAdministratorAsync(page, fixture.BaseAddress);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/ConcurrentWork?nodeId={leafId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.GetByRole(AriaRole.Heading, new() { Name = "Concurrent work" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Browse_offers_a_leaf_a_link_to_its_concurrent_work()
	{
		var (leafId, _, _) = await fixture.SeedFinishedSessionAsync("Linked concurrent work leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await BrowserTestSupport.SignInAdministratorAsync(page, fixture.BaseAddress);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");
		await page.GetByRole(AriaRole.Link, new() { Name = "Info" }).ClickAsync();

		await page.WaitForURLAsync(url => url.Contains("/Jobs/ConcurrentWork", StringComparison.Ordinal));
		(await page.GetByRole(AriaRole.Heading, new() { Name = "Concurrent work" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task The_concurrent_work_page_has_no_critical_or_serious_accessibility_violations()
	{
		var (leafId, _, _) = await fixture.SeedFinishedSessionAsync("Accessibility concurrent work leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await BrowserTestSupport.SignInAdministratorAsync(page, fixture.BaseAddress);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/ConcurrentWork?nodeId={leafId.Value}");

		BrowserTestSupport.AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/ConcurrentWork");
	}




}

public sealed class SqliteConcurrentWorkBrowserTests : ConcurrentWorkBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteConcurrentWorkBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlConcurrentWorkBrowserTests : ConcurrentWorkBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlConcurrentWorkBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
