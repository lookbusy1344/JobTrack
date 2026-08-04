namespace JobTrack.Web.EndToEndTests;

using System.Globalization;
using AwesomeAssertions;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for the responsive Awaiting Progress attention table.
/// </summary>
public abstract class AwaitingProgressBrowserTestsBase
{
	private const int TabletWidth = 768;
	private const int TabletHeight = 1024;
	private const int DesktopWidth = 1280;
	private const int DesktopHeight = 800;
	private const int WideDesktopWidth = 1440;
	private const int WideDesktopHeight = 900;

	/// <summary>
	///     Achievement renders an icon, not text, so at one twelfth the column was narrower than the
	///     word heading it at every width. It is gone: the state trails the row's name, as it does on
	///     Browse's subtree tables, and its share funds the description — the column that can always
	///     use more. Cost keeps the room it gained when Achievement dropped from two columns to one.
	/// </summary>
	private const double DescriptionMinimumShare = 4.5 / 12.0;

	private const double DescriptionMaximumShare = 5.5 / 12.0;

	/// <summary>
	///     Column allowance mirrors Browse's own child-nodes table: Description holds col-lg-5 at the
	///     desktop width and steps down to its "at least 3 of 12" floor (col-xxl-3) once xxl brings
	///     Priority back beside Deadline. Checked as a floor across the wide viewport matrix (1280 and
	///     1440), same as Browse's <c>MinimumWideDescriptionShare</c>, since the two widths land on
	///     different shares.
	/// </summary>
	private const double LargeDescriptionMinimumShare = 3.0 / 12.0;

	/// <summary>
	///     Two or more simultaneous workers' preview names need more than a twelfth of the row to avoid
	///     wrapping to a ladder of one-word lines -- the reported defect this column width fixes. Checked
	///     as a floor, not an exact range: with the Owner column gone, the browser's table layout
	///     redistributes its freed space rather than leaving it unused, so the actual share varies by
	///     viewport.
	/// </summary>
	private const double LargeActiveMinimumShare = 1.5 / 12.0;

	// The smallest count that has more than one worker to preview -- the plural-pill regression case.
	private const int TwoActiveWorkerCount = 2;

	private const string TwoActiveWorkerRowTitle = "Migrate the reporting warehouse's nightly ETL pipeline to the new managed cluster before renewal";

	// .status-pill's own comment documents a 24px single-line box; this leaves headroom for sub-pixel
	// rendering differences without letting a wrapped (multi-line) pill sneak past.
	private const float MaximumSingleLinePillHeightPixels = 32.0f;

	private readonly BrowserFixture fixture;

	protected AwaitingProgressBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> WideViewportMatrix => new() { { DesktopWidth, DesktopHeight }, { WideDesktopWidth, WideDesktopHeight } };

	[Fact]
	public async Task The_achievement_column_is_gone_and_its_share_widens_description_without_overflow()
	{
		_ = await fixture.SeedLeafAsync("Awaiting achievement width leaf");

		await using var context = await fixture.NewContextAsync(TabletWidth, TabletHeight);
		var page = await context.NewPageAsync();
		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/AwaitingProgress");

		var table = page.Locator("table.table");
		var achievementHeadings = await table
			.GetByRole(AriaRole.Columnheader, new() { Name = "Achievement", Exact = true })
			.CountAsync();
		achievementHeadings.Should().Be(0, "the achievement is drawn beside the row's name, not in a column of its own");
		var description = table.GetByRole(AriaRole.Columnheader, new() { Name = "Description", Exact = true });
		var tableWidth = await WidthAsync(table);
		var descriptionWidth = await WidthAsync(description);
		(descriptionWidth / tableWidth).Should().BeInRange(DescriptionMinimumShare, DescriptionMaximumShare,
			"Description should own five Bootstrap columns at the tablet breakpoint");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth,
			"the wider Description column should not introduce horizontal overflow");
	}

	[Theory]
	[MemberData(nameof(WideViewportMatrix))]
	/// <summary>
	///     Two simultaneous workers was already enough to break this row (regression case): the
	///     "N active" pill wrapped its word mid-character in the col-lg-1 the Active column used to get,
	///     and a long node title left no slack anywhere else to absorb it. The pill is now icon plus a
	///     bare count (data, not a word) and the column doubled to col-lg-2 -- proved together here at a
	///     realistic worst case rather than the short seeded title the test above uses.
	/// </summary>
	public async Task The_active_column_and_row_stay_intact_with_two_active_workers_and_a_long_title(int width, int height)
	{
		// Scoped to this test's own branch, not the shared class-fixture root: every test in this class
		// seeds under that root, and past the page's own PageSize the row would fall onto a later page
		// rather than appearing where the assertions below expect it -- the same reason
		// JobBrowseBrowserTests seeds its own branch for the subtree's BreadthCap.
		var branchId = await fixture.SeedBranchAsync($"Two-active worker robustness branch {width}");
		_ = await fixture.SeedActiveSessionsAsync(TwoActiveWorkerRowTitle, TwoActiveWorkerCount, branchId);

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();
		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"a long title with two active workers should not overflow at {width}x{height}");

		var table = page.Locator("table.table");
		var row = table.Locator("tbody tr", new() { HasTextString = TwoActiveWorkerRowTitle }).First;
		var activePill = row.Locator(".jt-col-active .status-pill-active");
		(await activePill.GetAttributeAsync("title")).Should().Be($"{TwoActiveWorkerCount} active");
		(await VisibleTextOfAsync(activePill)).Should().Be(TwoActiveWorkerCount.ToString(CultureInfo.InvariantCulture));

		var pillBox = await activePill.BoundingBoxAsync();
		pillBox.Should().NotBeNull();
		pillBox!.Height.Should().BeLessThanOrEqualTo(MaximumSingleLinePillHeightPixels,
			$"the active-count pill must stay a single line at {width}x{height}, not wrap its glyph or digit");

		var tableWidth = await WidthAsync(table);
		var descriptionWidth = await WidthAsync(table.GetByRole(AriaRole.Columnheader, new() { Name = "Description", Exact = true }));
		var activeWidth = await WidthAsync(table.GetByRole(AriaRole.Columnheader, new() { Name = "Active", Exact = true }));
		(descriptionWidth / tableWidth).Should().BeGreaterThanOrEqualTo(LargeDescriptionMinimumShare,
			$"Description must keep at least three of twelve columns at {width}x{height}");
		(activeWidth / tableWidth).Should().BeGreaterThanOrEqualTo(LargeActiveMinimumShare,
			$"Active must keep at least its widened column share at {width}x{height}");
	}

	/// <summary>
	///     The pill's own direct text nodes only -- not its nested visually-hidden accessible-name span,
	///     which <c>InnerTextAsync</c> is not reliable about excluding since it is clipped rather than
	///     <c>display: none</c>.
	/// </summary>
	private static async Task<string> VisibleTextOfAsync(ILocator element) =>
		await element.EvaluateAsync<string>(
			"el => Array.from(el.childNodes).filter(n => n.nodeType === 3).map(n => n.textContent.trim()).join('')");

	private static async Task<double> WidthAsync(ILocator element)
	{
		var box = await element.BoundingBoxAsync();
		box.Should().NotBeNull();
		return box!.Width;
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

public sealed class SqliteAwaitingProgressBrowserTests : AwaitingProgressBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteAwaitingProgressBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlAwaitingProgressBrowserTests : AwaitingProgressBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlAwaitingProgressBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
