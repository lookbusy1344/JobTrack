namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for the representative job-detail workflow (plan §8.5 slice 2, fix-plan
///     §2.5): sign-in through job tree browsing, covering the viewport matrix, keyboard operation,
///     visible focus, form validation, reflow at a 400%-zoom-equivalent width, and an automated
///     accessibility scan as a supplement to (not a replacement for) the checks above.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class JobBrowseBrowserTestsBase
{
	// Representative viewport matrix (plan §8.5/§8.7, fix-plan §2.5): small phone, large phone,
	// tablet, and desktop. 320 is WCAG 1.4.10 Reflow's minimum content-reflow width -- the
	// automatable equivalent of "400% zoom on a 1280px-wide desktop view" the plan asks for,
	// since Playwright has no notion of browser page zoom, only viewport size.
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

	protected JobBrowseBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_job_browse_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"the page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_to_a_320px_wide_viewport_keeps_content_and_controls_usable()
	{
		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.Locator("h1").First.IsVisibleAsync()).Should().BeTrue();
		(await page.Locator("nav.navbar").First.IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Signing_in_is_fully_operable_by_keyboard_with_visible_focus()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");

		// A freshly navigated page has no DOM focus at all, so the first Tab press has nothing to
		// advance from (document.activeElement is <body>, not the first tabbable control) unless
		// something claims page focus first -- document.body.focus() is the standard workaround.
		await page.EvaluateAsync("document.body.focus()");

		// The header nav (Home/Sign-in) precedes the login form in DOM order, so a real keyboard
		// user tabs through it first -- this proves the whole chain up to the username field is
		// reachable and focus-visible, not just the field in isolation.
		await TabToAsync(page, "Input_UserName", 10);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		await page.Keyboard.TypeAsync(BrowserFixture.AdministratorUserName);
		await page.Keyboard.PressAsync("Tab");
		var focusedAfterSecondTab = await page.EvaluateAsync<string>("document.activeElement.id");
		focusedAfterSecondTab.Should().Be("Input_Password");

		await page.Keyboard.TypeAsync(BrowserFixture.AdministratorPassword);
		await page.Keyboard.PressAsync("Enter");

		await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
		page.Url.Should().NotContain("/Account/Login");
	}

	[Fact]
	public async Task Submitting_the_login_form_with_missing_fields_shows_validation_messages()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		await page.Locator("button[type=submit]").ClickAsync();

		var usernameError = await page.Locator("span[data-valmsg-for='Input.UserName']").InnerTextAsync();
		var passwordError = await page.Locator("span[data-valmsg-for='Input.Password']").InnerTextAsync();

		usernameError.Should().NotBeNullOrWhiteSpace();
		passwordError.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task The_login_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");

		var results = await page.RunAxe();

		AssertNoCriticalOrSeriousViolations(results, "/Account/Login");
	}

	[Fact]
	public async Task The_recently_visited_section_is_visible_at_the_foot_of_the_browse_page()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var history = page.Locator("#jt-history");
		(await history.IsVisibleAsync()).Should().BeTrue();
		(await history.Locator(".jt-history-label").InnerTextAsync()).Should().Be("RECENTLY VISITED");
		(await history.Locator(".jt-history-list").IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Visiting_a_second_job_records_the_first_in_the_recently_visited_history()
	{
		var firstLeafId = await fixture.SeedLeafAsync("First visited job");
		var secondLeafId = await fixture.SeedLeafAsync("Second visited job");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={firstLeafId.Value}");
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={secondLeafId.Value}");

		var historyLink = page.Locator($"#jt-history-list a[href='/Jobs/Browse?nodeId={firstLeafId.Value}']");
		(await historyLink.InnerTextAsync()).Should().Be($"First visited job (ID {firstLeafId.Value})");
	}

	[Fact]
	public async Task Signing_out_clears_the_recently_visited_history_from_local_storage()
	{
		var leafId = await fixture.SeedLeafAsync("Job visited before sign-out");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");

		var historyBeforeSignOut = await page.EvaluateAsync<string?>("window.localStorage.getItem('jobtrack.history.v1')");
		historyBeforeSignOut.Should().NotBeNullOrEmpty("visiting a node records it in the recently-visited history");

		await page.Locator("form[data-jt-clear-history-on-submit] button[type=submit]").ClickAsync();
		await page.WaitForURLAsync(url => !url.Contains("/Jobs/Browse", StringComparison.Ordinal));

		var historyAfterSignOut = await page.EvaluateAsync<string?>("window.localStorage.getItem('jobtrack.history.v1')");
		historyAfterSignOut.Should()
			.BeNullOrEmpty("signing out must clear a signed-out account's breadcrumbs so they don't leak into the next session");
	}

	[Fact]
	public async Task Visiting_a_dead_breadcrumb_link_removes_it_from_the_recently_visited_history()
	{
		const long NonExistentNodeId = 9_999_999L;

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		await page.EvaluateAsync(
			"""
			window.localStorage.setItem('jobtrack.history.v1', JSON.stringify([
				{ id: '9999999', description: 'Deleted job', kind: 'Leaf' }
			]));
			""");

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={NonExistentNodeId}");
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var historyLink = page.Locator("#jt-history-list a", new() { HasTextString = "Deleted job" });
		(await historyLink.CountAsync()).Should().Be(0, "a breadcrumb pointing at a node that no longer exists should be dropped, not kept forever");
	}

	[Fact]
	/// <summary>
	/// On a phone the subtree table keeps what a person navigating a tree needs — where a row sits,
	/// what it is called, and how to start work on it — and drops the columns that would force the
	/// name into a two-character-wide column or the page into a horizontal scroll. The same columns
	/// are present again on a desktop viewport, so this is a reflow, not a permanent removal.
	/// </summary>
	public async Task The_subtree_table_drops_its_secondary_columns_on_a_phone_and_restores_them_on_desktop()
	{
		var branchId = await fixture.SeedBranchAsync("Kitchen renovation");
		_ = await fixture.SeedLeafAsync("Fit cabinets", branchId);

		await using var phoneContext = await fixture.NewContextAsync(SmallPhoneWidth, SmallPhoneHeight);
		var phone = await phoneContext.NewPageAsync();
		await SignInAsync(phone);
		await phone.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var phoneRow = phone.Locator("tbody tr", new() { HasTextString = "Fit cabinets" }).First;
		(await phoneRow.Locator(".jt-tree-name-link").First.IsVisibleAsync()).Should().BeTrue("the name is the point of the row");
		(await phoneRow.Locator(".jt-tree-icon").First.IsVisibleAsync()).Should().BeTrue("the kind glyph replaces the dropped Kind column");
		(await phoneRow.Locator("button", new() { HasTextString = "Start" }).First.IsVisibleAsync()).Should().BeTrue("starting work must stay reachable on a phone");
		(await phoneRow.Locator(".jt-col-secondary").First.IsVisibleAsync()).Should().BeFalse("owner/priority/cost/span are secondary on a phone");

		await using var desktopContext = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var desktop = await desktopContext.NewPageAsync();
		await SignInAsync(desktop);
		await desktop.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var desktopRow = desktop.Locator("tbody tr", new() { HasTextString = "Fit cabinets" }).First;
		(await desktopRow.Locator(".jt-col-secondary").First.IsVisibleAsync()).Should().BeTrue("the columns come back when there is room for them");
	}

	[Fact]
	public async Task The_job_browse_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var results = await page.RunAxe();

		AssertNoCriticalOrSeriousViolations(results, "/Jobs/Browse");
	}

	private static void AssertNoCriticalOrSeriousViolations(AxeResult results, string pageName)
	{
		var criticalOrSerious = results.Violations
			.Where(violation => violation.Impact is "critical" or "serious")
			.ToArray();

		criticalOrSerious.Should().BeEmpty(
			$"{pageName} should have no critical/serious accessibility violations, found: " +
			string.Join("; ", criticalOrSerious.Select(v => $"{v.Id} ({v.Impact}): {v.Help}")));
	}

	private static async Task TabToAsync(IPage page, string targetElementId, int maxTabs)
	{
		for (var attempt = 0; attempt < maxTabs; attempt++) {
			await page.Keyboard.PressAsync("Tab");
			var focusedId = await page.EvaluateAsync<string>("document.activeElement.id");
			if (focusedId == targetElementId) {
				return;
			}
		}

		throw new InvalidOperationException($"Tabbing {maxTabs} times from the page load never reached '#{targetElementId}'.");
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

public sealed class SqliteJobBrowseBrowserTests : JobBrowseBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteJobBrowseBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlJobBrowseBrowserTests : JobBrowseBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlJobBrowseBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
