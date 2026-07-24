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
	private const int RequiredSimultaneousWorkerCount = 3;

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
	public async Task A_branch_reads_Unfinished_while_one_of_its_leaves_has_not_succeeded()
	{
		var branchId = await fixture.SeedBranchAsync("Unfinished branch");
		_ = await fixture.SeedSuccessLeafAsync("Succeeded leaf", branchId);
		_ = await fixture.SeedLeafAsync("Outstanding leaf", branchId);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		(await page.Locator(".jt-achievement-icon--waiting .jt-achievement-icon-label").InnerTextAsync()).Should().Be("Unfinished");
	}

	[Fact]
	public async Task A_branch_reads_Success_once_every_leaf_in_its_subtree_has_succeeded()
	{
		var branchId = await fixture.SeedBranchAsync("Fully succeeded branch");
		_ = await fixture.SeedSuccessLeafAsync("First succeeded leaf", branchId);
		_ = await fixture.SeedSuccessLeafAsync("Second succeeded leaf", branchId);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		(await page.Locator(".jt-achievement-icon--success .jt-achievement-icon-label").InnerTextAsync()).Should().Be("Success");
	}

	[Fact]
	public async Task A_branch_of_branches_reads_Success_only_once_every_descendant_leaf_has_succeeded()
	{
		var outerBranchId = await fixture.SeedBranchAsync("Outer branch");
		var innerBranchId = await fixture.SeedBranchAsync("Inner branch", outerBranchId);
		_ = await fixture.SeedSuccessLeafAsync("Inner branch's succeeded leaf", innerBranchId);
		_ = await fixture.SeedLeafAsync("Outer branch's own outstanding leaf", outerBranchId);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={outerBranchId.Value}");

		(await page.Locator(".jt-achievement-icon--waiting .jt-achievement-icon-label").InnerTextAsync()).Should().Be("Unfinished");
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
		(await phoneRow.Locator("button", new() { HasTextString = "Start" }).First.IsVisibleAsync()).Should()
			.BeTrue("starting work must stay reachable on a phone");
		(await phoneRow.Locator(".jt-col-secondary").First.IsVisibleAsync()).Should().BeFalse("owner/priority/cost/span are secondary on a phone");

		await using var desktopContext = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var desktop = await desktopContext.NewPageAsync();
		await SignInAsync(desktop);
		await desktop.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var desktopRow = desktop.Locator("tbody tr", new() { HasTextString = "Fit cabinets" }).First;
		(await desktopRow.Locator(".jt-col-secondary").First.IsVisibleAsync()).Should().BeTrue("the columns come back when there is room for them");
	}

	[Fact]
	public async Task The_active_column_reflows_off_phone_width_while_session_actions_remain_available()
	{
		_ = await fixture.SeedActiveSessionsAsync("Responsive active worker leaf", RequiredSimultaneousWorkerCount);

		await using var phoneContext = await fixture.NewContextAsync(SmallPhoneWidth, SmallPhoneHeight);
		var phone = await phoneContext.NewPageAsync();
		await SignInAsync(phone);
		await phone.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var phoneRow = phone.Locator("tbody tr", new() { HasTextString = "Responsive active worker leaf" }).First;
		(await phoneRow.Locator(".jt-col-active").IsVisibleAsync()).Should().BeFalse();
		(await phoneRow.GetByRole(AriaRole.Link, new() { Name = "Sessions", Exact = true }).IsVisibleAsync()).Should().BeTrue();

		await using var desktopContext = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var desktop = await desktopContext.NewPageAsync();
		await SignInAsync(desktop);
		await desktop.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={fixture.RootJobNodeId.Value}");

		var desktopRow = desktop.Locator("tbody tr", new() { HasTextString = "Responsive active worker leaf" }).First;
		(await desktopRow.Locator(".jt-col-active").IsVisibleAsync()).Should().BeTrue();
		(await desktopRow.GetByText($"{RequiredSimultaneousWorkerCount} active", new() { Exact = true }).IsVisibleAsync()).Should().BeTrue();
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

	/// <summary>
	///     The Requires/Depends-on-this-job card's rows must line up regardless of content or which
	///     side of the card they're on: an empty side ("None."), a satisfied prerequisite (go glyph),
	///     a blocking one (stop glyph), and a plain dependent row (no glyph -- only Requires ever
	///     carries a readiness marker) all render through the same <c>.jt-list &gt; li</c> box, so none
	///     of them should stand taller than another (row-height regression guard for the icon-scale
	///     fix below). The card is hidden entirely when both sides are empty, so a "None." row is only
	///     reachable on a node with exactly one populated side: the satisfied prerequisite has no
	///     requirements of its own (Requires = "None.", one populated Depends-on row), and the
	///     grand-dependent has no dependents of its own (Depends-on = "None.", one populated Requires
	///     row). A third page load, for the node with both prerequisite outcomes, supplies "Blocked",
	///     "Unblocked", and a second Depends-on-this-job comparison row.
	/// </summary>
	[Fact]
	public async Task Requires_and_depends_on_rows_are_all_the_same_height_whether_empty_blocked_or_unblocked()
	{
		var satisfiedRequiredId = await fixture.SeedSuccessLeafAsync("Foundation poured");
		var blockingRequiredId = await fixture.SeedLeafAsync("Wiring not done");
		var dependentId = await fixture.SeedLeafAsync("Fit cabinets");
		var grandDependentId = await fixture.SeedLeafAsync("Hang cabinet doors");
		await fixture.SeedPrerequisiteAsync(satisfiedRequiredId, dependentId);
		await fixture.SeedPrerequisiteAsync(blockingRequiredId, dependentId);
		await fixture.SeedPrerequisiteAsync(dependentId, grandDependentId);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		await SignInAsync(page);

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={satisfiedRequiredId.Value}");
		var noneRequiresHeight = await RowHeightAsync(page.Locator(".jt-card .jt-prereq-col").Nth(0).Locator("li"));

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={grandDependentId.Value}");
		var noneDependsOnHeight = await RowHeightAsync(page.Locator(".jt-card .jt-prereq-col").Nth(1).Locator("li"));

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={dependentId.Value}");
		var columns = page.Locator(".jt-card .jt-prereq-col");
		var requiresRows = columns.Nth(0).Locator("li");
		var dependsOnRows = columns.Nth(1).Locator("li");

		(await requiresRows.CountAsync()).Should().Be(2, "one satisfied (unblocked) and one blocking prerequisite");
		(await dependsOnRows.CountAsync()).Should().Be(1, "one job depends on this leaf");

		(await requiresRows.Nth(0).InnerHTMLAsync()).Should().Contain("Satisfied", "the successful prerequisite was added first");
		(await requiresRows.Nth(1).InnerHTMLAsync()).Should().Contain("Blocking", "the unfinished prerequisite was added second");
		var unblockedHeight = await RowHeightAsync(requiresRows.Nth(0));
		var blockedHeight = await RowHeightAsync(requiresRows.Nth(1));
		var populatedDependsOnHeight = await RowHeightAsync(dependsOnRows.Nth(0));

		var heights = new Dictionary<string, double> {
			["None (Requires)"] = noneRequiresHeight,
			["None (Depends-on)"] = noneDependsOnHeight,
			["Unblocked (Requires)"] = unblockedHeight,
			["Blocked (Requires)"] = blockedHeight,
			["Populated (Depends-on)"] = populatedDependsOnHeight,
		};

		var max = heights.Values.Max();
		var min = heights.Values.Min();
		(max - min).Should().BeLessThanOrEqualTo(1.0,
			"every row -- empty, blocked, unblocked, or a plain dependent -- should be the same height, but got: " +
			string.Join(", ", heights.Select(kv => $"{kv.Key}={kv.Value}")));
	}

	private static async Task<double> RowHeightAsync(ILocator row)
	{
		var box = await row.BoundingBoxAsync();
		box.Should().NotBeNull();
		return box!.Height;
	}

	/// <summary>
	///     Requires and Depends-on-this-job are Bootstrap grid columns (<c>col-md-6</c>), a fixed
	///     50/50 split of the card -- not a flex-basis derived from each side's content, which let a
	///     long node title on one side outweigh a short one on the other. Checked both ways round (the
	///     long title on Requires, then on Depends-on) so the assertion cannot pass by coincidence of
	///     which side happens to be first in DOM order.
	/// </summary>
	[Fact]
	public async Task Requires_and_depends_on_columns_stay_equal_width_regardless_of_which_side_has_the_longer_title()
	{
		const string LongTitle =
			"A very long prerequisite job title that would visually widen its column if the layout were content-driven instead of a fixed 50/50 split";

		var longRequiredId = await fixture.SeedLeafAsync(LongTitle);
		var shortDependentId = await fixture.SeedLeafAsync("Y");
		var currentId = await fixture.SeedLeafAsync("Current node under test");
		await fixture.SeedPrerequisiteAsync(longRequiredId, currentId);
		await fixture.SeedPrerequisiteAsync(currentId, shortDependentId);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		await SignInAsync(page);

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={currentId.Value}");
		var columns = page.Locator(".jt-card .jt-prereq-col");
		var requiresWidth = await ColumnWidthAsync(columns.Nth(0));
		var dependsOnWidth = await ColumnWidthAsync(columns.Nth(1));

		Math.Abs(requiresWidth - dependsOnWidth).Should().BeLessThanOrEqualTo(1.0,
			"the long-titled prerequisite is on the Requires side, but both columns should still split the card " +
			$"50/50, got Requires={requiresWidth}, Depends-on={dependsOnWidth}");

		var swappedCurrentId = await fixture.SeedLeafAsync("Second current node under test");
		var shortRequiredId = await fixture.SeedLeafAsync("Z");
		var longDependentId = await fixture.SeedLeafAsync(LongTitle);
		await fixture.SeedPrerequisiteAsync(shortRequiredId, swappedCurrentId);
		await fixture.SeedPrerequisiteAsync(swappedCurrentId, longDependentId);

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={swappedCurrentId.Value}");
		var swappedColumns = page.Locator(".jt-card .jt-prereq-col");
		var swappedRequiresWidth = await ColumnWidthAsync(swappedColumns.Nth(0));
		var swappedDependsOnWidth = await ColumnWidthAsync(swappedColumns.Nth(1));

		Math.Abs(swappedRequiresWidth - swappedDependsOnWidth).Should().BeLessThanOrEqualTo(1.0,
			"the long-titled dependent is on the Depends-on side this time, but both columns should still split the " +
			$"card 50/50, got Requires={swappedRequiresWidth}, Depends-on={swappedDependsOnWidth}");
	}

	private static async Task<double> ColumnWidthAsync(ILocator column)
	{
		var box = await column.BoundingBoxAsync();
		box.Should().NotBeNull();
		return box!.Width;
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
