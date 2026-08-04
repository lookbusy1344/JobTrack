namespace JobTrack.Web.EndToEndTests;

using System.Globalization;
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
/// <summary>
///     Which point of an element a hit test samples: its middle, or just inside its bottom edge (the edge
///     an ancestor's clipping or a later sibling's paint order takes away first).
/// </summary>
public enum SamplePoint
{
	Centre,
	BottomEdge,
}

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
	private const int LaptopWidth = 1024;
	private const int LaptopHeight = 768;
	private const int DesktopWidth = 1280;
	private const int DesktopHeight = 800;
	private const int WideDesktopWidth = 1440;
	private const int WideDesktopHeight = 900;
	private const int ReflowWidth = 320;
	private const int ReflowHeight = 640;

	// What TopmostAtAsync reports when the hit test lands on the element it was asked about; anything
	// else it returns is a description of whatever was painted over it.
	private const string TopmostIsExpectedElement = "the expected element";

	// Hit-testing a floating panel samples just inside its own bottom edge rather than exactly on it:
	// the edge pixel itself belongs to the border, and sub-pixel bounding-box rounding can land the
	// sample one device pixel outside the element altogether.
	private const double PopoverEdgeSampleInset = 4.0;
	private const double MaximumInlineStatusGapPixels = 8.0;
	private const float MaximumColumnAlignmentDifferencePixels = 1.0f;
	private const double NarrowDescriptionMinimumShare = 9.0 / 12.0;
	private const double NarrowDescriptionMaximumShare = 10.0 / 12.0;

	/// <summary>
	///     Description yields one column to Cost at the tablet breakpoint. Cost renders
	///     "£1,234.56 / 12h 30m", whose four break opportunities made it the column auto table layout
	///     squeezed first, collapsing it toward a character per line while Description kept its room;
	///     it holds three columns here instead. Description still owns by far the largest share, and
	///     the narrow and wide bands below are unchanged.
	/// </summary>
	private const double MediumDescriptionMinimumShare = 4.5 / 12.0;

	private const double MediumDescriptionMaximumShare = 5.0 / 12.0;

	/// <summary>
	///     From the laptop breakpoint up to xxl, Description takes the two twelfths Priority and
	///     Deadline give up (col-lg-5): Priority is one tap away on the row's own page, and Deadline
	///     renders "d MMM"/"HH:mm", which needs a twelfth at most. Both are worth less at a glance than
	///     eight more characters of the node's own name.
	/// </summary>
	private const double LargeDescriptionMinimumShare = 4.5 / 12.0;

	private const double LargeDescriptionMaximumShare = 5.0 / 12.0;

	/// <summary>
	///     Description returns to its "at least 3 of 12" floor once xxl brings Priority back beside the
	///     span bar. Active also holds the column Description gave up at lg (col-lg-1 -> col-lg-2, plan
	///     follow-up), so two or more simultaneous workers' names have more than a twelfth of the row to
	///     preview in instead of wrapping to a ladder of one-word lines.
	/// </summary>
	private const double WideDescriptionMinimumShare = 2.5 / 12.0;

	private const double WideDescriptionMaximumShare = 3.0 / 12.0;

	/// <summary>
	///     Cost holds three columns at the tablet breakpoint and two once the laptop width returns the
	///     secondary columns. It renders "£1,234.56 / 12h 30m", whose four break opportunities made it
	///     the column auto table layout squeezed first — an earlier flat two-column allocation measured
	///     correctly here while the body text still wrapped toward a character per line, which is why
	///     the test now checks the rendered cell as well as the heading's share.
	/// </summary>
	private const double TabletCostMinimumShare = 2.5 / 12.0;

	private const double TabletCostMaximumShare = 3.0 / 12.0;
	private const double LaptopCostMinimumShare = 1.5 / 12.0;
	private const double LaptopCostMaximumShare = 2.0 / 12.0;
	// The floor that holds across the whole wide band (1280 and 1440): at 1280 Description has the
	// twelfth Priority gives up, at 1440 it is back at its three-of-twelve floor.
	private const double MinimumWideDescriptionShare = WideDescriptionMinimumShare;

	// The smallest count that has more than one worker to preview -- the plural-pill regression case.
	private const int TwoActiveWorkerCount = 2;

	private const string TwoActiveWorkerRowTitle = "Migrate the reporting warehouse's nightly ETL pipeline to the new managed cluster before renewal";

	// .status-pill's own comment documents a 24px single-line box; this leaves headroom for
	// sub-pixel rendering differences without letting a wrapped (multi-line) pill sneak past.
	private const float MaximumSingleLinePillHeightPixels = 32.0f;

	private readonly BrowserFixture fixture;

	protected JobBrowseBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	public static TheoryData<int, int> WideViewportMatrix => new() { { DesktopWidth, DesktopHeight }, { WideDesktopWidth, WideDesktopHeight } };

	public static TheoryData<int, int, int, double, double> DescriptionColumnViewportMatrix => new() {
		{ ReflowWidth, ReflowHeight, 2, NarrowDescriptionMinimumShare, NarrowDescriptionMaximumShare },
		{ SmallPhoneWidth, SmallPhoneHeight, 2, NarrowDescriptionMinimumShare, NarrowDescriptionMaximumShare },
		{ LargePhoneWidth, LargePhoneHeight, 2, NarrowDescriptionMinimumShare, NarrowDescriptionMaximumShare },
		{ TabletWidth, TabletHeight, 4, MediumDescriptionMinimumShare, MediumDescriptionMaximumShare },
		{ LaptopWidth, LaptopHeight, 5, LargeDescriptionMinimumShare, LargeDescriptionMaximumShare },
		{ DesktopWidth, DesktopHeight, 5, LargeDescriptionMinimumShare, LargeDescriptionMaximumShare },
		{ WideDesktopWidth, WideDesktopHeight, 7, WideDescriptionMinimumShare, WideDescriptionMaximumShare },
	};

	public static TheoryData<int, int, bool> BlockerColumnViewportMatrix => new() {
		{ ReflowWidth, ReflowHeight, true }, { DesktopWidth, DesktopHeight, false },
	};

	public static TheoryData<int, int, double, double> MidWidthCostViewportMatrix => new() {
		{ TabletWidth, TabletHeight, TabletCostMinimumShare, TabletCostMaximumShare },
		{ LaptopWidth, LaptopHeight, LaptopCostMinimumShare, LaptopCostMaximumShare },
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
	/// <summary>
	/// WCAG 1.4.4 (Resize Text) requires the page to scale to at least 200%. A viewport meta that
	/// pins <c>maximum-scale</c> or sets <c>user-scalable=no</c> takes pinch-zoom away from every
	/// touch reader, and axe rates it a *critical* <c>meta-viewport</c> violation. The layout is
	/// shared, so proving it once here covers every page.
	/// </summary>
	public async Task The_viewport_meta_leaves_pinch_zoom_available()
	{
		await using var context = await fixture.NewContextAsync(SmallPhoneWidth, SmallPhoneHeight);
		var page = await context.NewPageAsync();

		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");

		var viewport = await page.Locator("meta[name=viewport]").GetAttributeAsync("content");

		viewport.Should().NotBeNull();
		viewport!.Should().NotContain("user-scalable", "disabling zoom fails WCAG 1.4.4");
		viewport.Should().NotContain("maximum-scale", "capping the scale factor fails WCAG 1.4.4");
		viewport.Should().NotContain("minimum-scale", "pinning the scale floor is the same defect from the other side");
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
	public async Task Clearing_the_recently_visited_history_empties_the_list_and_local_storage()
	{
		var firstLeafId = await fixture.SeedLeafAsync("First clearable job");
		var secondLeafId = await fixture.SeedLeafAsync("Second clearable job");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={firstLeafId.Value}");
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={secondLeafId.Value}");
		(await page.Locator($"#jt-history-list a[href='/Jobs/Browse?nodeId={firstLeafId.Value}']").CountAsync()).Should()
			.Be(1, "the first job is now a breadcrumb");

		await page.Locator("#jt-history-clear").ClickAsync();

		(await page.Locator("#jt-history-list a").CountAsync()).Should().Be(0);
		(await page.Locator(".jt-history-empty").InnerTextAsync()).Should().Be("None yet.");
		var stored = await page.EvaluateAsync<string?>("window.localStorage.getItem('jobtrack.history.v1')");
		stored.Should().BeNullOrEmpty("clearing means clearing -- the current node's own breadcrumb goes too");
	}

	[Fact]
	public async Task A_cleared_history_stays_cleared_and_starts_recording_again_from_the_next_visit()
	{
		var firstLeafId = await fixture.SeedLeafAsync("Forgotten job");
		var secondLeafId = await fixture.SeedLeafAsync("Job open when cleared");
		var thirdLeafId = await fixture.SeedLeafAsync("First job after the clear");
		var fourthLeafId = await fixture.SeedLeafAsync("Second job after the clear");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={firstLeafId.Value}");
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={secondLeafId.Value}");
		await page.Locator("#jt-history-clear").ClickAsync();

		// The node open when Clear was pressed is cleared along with everything else -- it is only
		// re-recorded if it is visited again, so it must not reappear as a breadcrumb here.
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={thirdLeafId.Value}");
		(await page.Locator("#jt-history-list a").CountAsync()).Should().Be(0);

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={fourthLeafId.Value}");
		var links = page.Locator("#jt-history-list a");
		(await links.CountAsync()).Should().Be(1, "recording restarts from the first visit after the clear");
		(await links.First.InnerTextAsync()).Should().Be($"First job after the clear (ID {thirdLeafId.Value})");
	}

	[Fact]
	public async Task The_clear_link_is_hidden_while_there_is_nothing_to_clear()
	{
		var leafId = await fixture.SeedLeafAsync("Only job visited");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		// Sign-in itself lands on Browse and records that node, so start from a genuinely empty
		// history rather than from whatever the sign-in redirect happened to leave behind.
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");
		await page.EvaluateAsync("window.localStorage.removeItem('jobtrack.history.v1')");
		await page.ReloadAsync();

		// The only breadcrumb is now the node being looked at, which the list never shows -- so there
		// is visibly nothing to clear, and an enabled Clear link would be a control with no effect.
		(await page.Locator("#jt-history-clear").IsVisibleAsync()).Should().BeFalse();
		(await page.Locator(".jt-history-empty").IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task The_clear_link_is_operable_by_keyboard_and_leaves_the_page_accessible()
	{
		var firstLeafId = await fixture.SeedLeafAsync("First keyboard-cleared job");
		var secondLeafId = await fixture.SeedLeafAsync("Second keyboard-cleared job");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={firstLeafId.Value}");
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={secondLeafId.Value}");

		var clear = page.GetByRole(AriaRole.Button, new() { Name = "Clear recently visited", Exact = true });
		await clear.FocusAsync();
		(await page.EvaluateAsync<string>("document.activeElement.id")).Should().Be("jt-history-clear");
		await clear.PressAsync("Enter");

		(await page.Locator("#jt-history-list a").CountAsync()).Should().Be(0);
		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Browse after clearing the history");
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
	/// what it is called, and the one way off the row (Sessions, which doubles as Browse-to-child-and-
	/// view) — and drops the columns and row actions that would force the name into a two-character-
	/// wide column or the page into a horizontal scroll. The same columns and actions are present
	/// again on a desktop viewport, so this is a reflow, not a permanent removal.
	/// </summary>
	public async Task The_subtree_table_drops_its_secondary_columns_on_a_phone_and_restores_them_on_desktop()
	{
		var branchId = await fixture.SeedBranchAsync("Kitchen renovation");
		_ = await fixture.SeedLeafAsync("Fit cabinets", branchId);

		await using var phoneContext = await fixture.NewContextAsync(SmallPhoneWidth, SmallPhoneHeight);
		var phone = await phoneContext.NewPageAsync();
		await SignInAsync(phone);
		// Rooted at this test's own branch, not the shared fixture root: every test in this class seeds
		// under that root, so past JobSubtreeLimits.BreadthCap children the root view would truncate this
		// leaf away and the assertions below would read as a reflow failure.
		await phone.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var phoneRow = phone.Locator("tbody tr", new() { HasTextString = "Fit cabinets" }).First;
		(await phoneRow.Locator(".jt-tree-name-link").First.IsVisibleAsync()).Should().BeTrue("the name is the point of the row");
		(await phoneRow.Locator(".jt-tree-icon").First.IsVisibleAsync()).Should().BeTrue("the kind glyph replaces the dropped Kind column");
		(await phoneRow.GetByRole(AriaRole.Link, new() { Name = "Sessions", Exact = true }).IsVisibleAsync()).Should()
			.BeTrue("Sessions is the one row action that must stay reachable on a phone");
		(await phoneRow.Locator("button", new() { HasTextString = "Start" }).First.IsVisibleAsync()).Should()
			.BeFalse("Start is one tap away via Sessions/Browse and would crowd a phone-width row");
		(await phoneRow.Locator(".jt-col-secondary").Last.IsVisibleAsync()).Should().BeFalse("owner/priority/cost/span are secondary on a phone");

		await using var desktopContext = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var desktop = await desktopContext.NewPageAsync();
		await SignInAsync(desktop);
		await desktop.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var desktopRow = desktop.Locator("tbody tr", new() { HasTextString = "Fit cabinets" }).First;
		// .Last, not .First: the first .jt-col-secondary in the row is Priority, which this table alone
		// holds back to xxl so Description can have its twelfth. Deadline is the one that returns at lg.
		(await desktopRow.Locator(".jt-col-secondary").Last.IsVisibleAsync()).Should().BeTrue("the columns come back when there is room for them");
		(await desktopRow.Locator("button", new() { HasTextString = "Start" }).First.IsVisibleAsync()).Should()
			.BeTrue("Start comes back when there is room for it");
	}

	[Theory]
	[MemberData(nameof(DescriptionColumnViewportMatrix))]
	public async Task The_subtree_description_column_owns_the_available_width(
		int width, int height, int expectedVisibleColumns, double minimumDescriptionShare, double maximumDescriptionShare)
	{
		var branchId = await fixture.SeedBranchAsync("Description width branch");
		_ = await fixture.SeedLeafAsync("Brief title", branchId);

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();
		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var table = page.Locator(".jt-browse-children-table");
		var headings = table.GetByRole(AriaRole.Columnheader);
		var visibleColumnCount = await headings.EvaluateAllAsync<int>(
			"elements => elements.filter(element => element.getClientRects().length > 0).length");
		var tableWidth = await ColumnWidthAsync(table);
		var descriptionWidth = await ColumnWidthAsync(
			table.GetByRole(AriaRole.Columnheader, new() { Name = "Description", Exact = true }));

		visibleColumnCount.Should().Be(expectedVisibleColumns);
		(descriptionWidth / tableWidth).Should().BeInRange(minimumDescriptionShare, maximumDescriptionShare,
			$"Description should follow the responsive Bootstrap column allocation when Browse shows {expectedVisibleColumns} columns at {width}px");
	}

	[Theory]
	[MemberData(nameof(MidWidthCostViewportMatrix))]
	public async Task The_subtree_cost_column_has_a_readable_mid_width_allocation(
		int width, int height, double minimumCostShare, double maximumCostShare)
	{
		var branchId = await fixture.SeedBranchAsync($"Cost width branch {width}");
		_ = await fixture.SeedLeafAsync($"Cost width leaf {width}", branchId);

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();
		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var table = page.Locator(".jt-browse-children-table");
		var tableWidth = await ColumnWidthAsync(table);
		var costWidth = await ColumnWidthAsync(table.GetByRole(AriaRole.Columnheader, new() { Name = "Cost", Exact = true }));

		(costWidth / tableWidth).Should().BeInRange(minimumCostShare, maximumCostShare,
			$"Cost should follow its responsive Bootstrap column allocation at the {width}px mid-width viewport");

		// The allocation above is necessary but not sufficient. "£1,234.56 / 12h 30m" carries four
		// break opportunities, so under auto table layout cost is the column the browser squeezes
		// first: it can measure its full share here and still wrap toward a character per line. The
		// seeded costs are too short to reproduce that, so pin the mechanism that prevents it rather
		// than a symptom this fixture cannot produce.
		var costWhiteSpace = await table.Locator("tbody td.jt-col-cost").First.EvaluateAsync<string>(
			"cell => getComputedStyle(cell).whiteSpace");
		costWhiteSpace.Should().Be("nowrap",
			$"cost and its allocated duration must stay atomic at {width}px so the column cannot be squeezed below them");
	}

	[Theory]
	[MemberData(nameof(BlockerColumnViewportMatrix))]
	public async Task Inherited_blocker_groups_stack_on_phones_and_share_the_row_on_desktop(int width, int height, bool expectedStacked)
	{
		var branchId = await fixture.SeedBranchAsync($"Inherited blocker branch {width}");
		var leafId = await fixture.SeedLeafAsync($"Inherited blocker child {width}", branchId);
		var requiredId = await fixture.SeedLeafAsync($"Required inherited job {width}");
		await fixture.SeedPrerequisiteAsync(requiredId, branchId);

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();
		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");
		await page.GetByText("Inherited blockers (from ancestor prerequisites)", new() { Exact = true }).ClickAsync();

		var groups = page.Locator("details.jt-card .row > .col-12.col-md-6");
		(await groups.CountAsync()).Should().Be(2);
		var first = await groups.Nth(0).BoundingBoxAsync();
		var second = await groups.Nth(1).BoundingBoxAsync();
		first.Should().NotBeNull();
		second.Should().NotBeNull();

		if (expectedStacked) {
			second!.Y.Should().BeGreaterThan(first!.Y + first.Height,
				"Bootstrap col-12 groups should stack with a gutter at phone width");
		} else {
			Math.Abs(second!.Y - first!.Y).Should().BeLessThanOrEqualTo(MaximumColumnAlignmentDifferencePixels,
				"Bootstrap col-md-6 groups should share one row at desktop width");
			Math.Abs(second.Width - first.Width).Should().BeLessThanOrEqualTo(MaximumColumnAlignmentDifferencePixels,
				"the inherited-blocker groups should split the row equally");
		}

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth,
			$"the inherited-blocker layout should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task The_active_column_reflows_off_phone_width_while_session_actions_remain_available()
	{
		var branchId = await fixture.SeedBranchAsync("Responsive active worker branch");
		_ = await fixture.SeedActiveSessionsAsync("Responsive active worker leaf", RequiredSimultaneousWorkerCount, branchId);

		await using var phoneContext = await fixture.NewContextAsync(SmallPhoneWidth, SmallPhoneHeight);
		var phone = await phoneContext.NewPageAsync();
		await SignInAsync(phone);
		// This test's own branch rather than the shared fixture root, for the JobSubtreeLimits.BreadthCap
		// reason given on the secondary-column reflow test above.
		await phone.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var phoneRow = phone.Locator("tbody tr", new() { HasTextString = "Responsive active worker leaf" }).First;
		(await phoneRow.Locator(".jt-col-active").IsVisibleAsync()).Should().BeFalse();
		(await phoneRow.GetByRole(AriaRole.Link, new() { Name = "Sessions", Exact = true }).IsVisibleAsync()).Should().BeTrue();

		await using var desktopContext = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var desktop = await desktopContext.NewPageAsync();
		await SignInAsync(desktop);
		await desktop.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var desktopRow = desktop.Locator("tbody tr", new() { HasTextString = "Responsive active worker leaf" }).First;
		(await desktopRow.Locator(".jt-col-active").IsVisibleAsync()).Should().BeTrue();
		// The table-cell pill is compact -- glyph and bare count only, per _ActiveSincePill's Compact
		// branch -- with the full wording carried in its title tooltip rather than rendered text.
		var activePill = desktopRow.Locator(".jt-col-active .status-pill-active");
		(await activePill.GetAttributeAsync("title")).Should().Be($"{RequiredSimultaneousWorkerCount} active");
		(await VisibleTextOfAsync(activePill)).Should().Be(RequiredSimultaneousWorkerCount.ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>
	///     The pill's own direct text nodes only -- not its nested visually-hidden accessible-name span,
	///     which <c>InnerTextAsync</c> is not reliable about excluding since it is clipped rather than
	///     <c>display: none</c>.
	/// </summary>
	private static async Task<string> VisibleTextOfAsync(ILocator element) =>
		await element.EvaluateAsync<string>(
			"el => Array.from(el.childNodes).filter(n => n.nodeType === 3).map(n => n.textContent.trim()).join('')");

	[Theory]
	[MemberData(nameof(WideViewportMatrix))]
	/// <summary>
	///     Two simultaneous workers was already enough to break this row (regression case): the
	///     "N active" pill wrapped its word mid-character in the col-lg-1 the Active column used to get,
	///     and a long node title left no slack anywhere else to absorb it. The pill is now icon plus a
	///     bare count (data, not a word) and the column doubled to col-lg-2, funded by Description
	///     stepping down to its documented "at least 3 of 12" floor -- proved together here at a
	///     realistic worst case rather than the short seeded titles the other column tests use.
	/// </summary>
	public async Task The_subtree_active_pill_and_row_stay_intact_with_two_active_workers_and_a_long_title(int width, int height)
	{
		var branchId = await fixture.SeedBranchAsync($"Two-active worker robustness branch {width}");
		_ = await fixture.SeedActiveSessionsAsync(TwoActiveWorkerRowTitle, TwoActiveWorkerCount, branchId);

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();
		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"a long title with two active workers should not overflow at {width}x{height}");

		var row = page.Locator("tbody tr", new() { HasTextString = TwoActiveWorkerRowTitle }).First;
		var activePill = row.Locator(".jt-col-active .status-pill-active");
		(await activePill.GetAttributeAsync("title")).Should().Be($"{TwoActiveWorkerCount} active");
		(await VisibleTextOfAsync(activePill)).Should().Be(TwoActiveWorkerCount.ToString(CultureInfo.InvariantCulture));

		var pillBox = await activePill.BoundingBoxAsync();
		pillBox.Should().NotBeNull();
		pillBox!.Height.Should().BeLessThanOrEqualTo(MaximumSingleLinePillHeightPixels,
			$"the active-count pill must stay a single line at {width}x{height}, not wrap its glyph or digit");

		var table = page.Locator(".jt-browse-children-table");
		var tableWidth = await ColumnWidthAsync(table);
		var descriptionWidth = await ColumnWidthAsync(table.GetByRole(AriaRole.Columnheader, new() { Name = "Description", Exact = true }));
		(descriptionWidth / tableWidth).Should().BeGreaterThanOrEqualTo(MinimumWideDescriptionShare,
			$"the node title must keep at least three of twelve columns at {width}x{height}");
	}

	[Fact]
	/// <summary>
	/// A wrapped title whose final line is shorter than its first must not reserve the first line's
	/// width before rendering its achievement icon.
	/// </summary>
	public async Task A_childs_achievement_icon_immediately_follows_its_rendered_title()
	{
		const string Description = "Integration test for the legacy MIS write-back adapter under representative load X";
		var (leafId, _) = await fixture.SeedActiveSessionsAsync(Description, 1);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={fixture.RootJobNodeId.Value}");

		var rowText = page.Locator($".jt-tree-name-link[href='/Jobs/Browse?nodeId={leafId.Value}']").Locator("..");
		var measurements = await rowText.EvaluateAsync<double[]>(
			"""
			(element) => {
				const link = element.querySelector(".jt-tree-name-link");
				const icon = element.querySelector(".jt-achievement-icon");
				const range = document.createRange();
				range.selectNodeContents(link.firstChild);
				const textRects = Array.from(range.getClientRects());
				const finalTextRect = textRects.at(-1);
				const iconRect = icon.getBoundingClientRect();
				return [
					iconRect.left - finalTextRect.right,
					Math.min(iconRect.bottom, finalTextRect.bottom) - Math.max(iconRect.top, finalTextRect.top),
					textRects.length,
				];
			}
			""");

		measurements[2].Should().BeGreaterThan(1, "the regression case must exercise a naturally wrapped title");
		measurements[0].Should().BeInRange(0, MaximumInlineStatusGapPixels,
			"the status icon should sit immediately after the title's final rendered glyph");
		measurements[1].Should().BeGreaterThan(0,
			"the status icon should overlap the title's final rendered line vertically");
	}

	[Fact]
	/// <summary>
	/// On a phone the leaf's own Sessions table (_LeafWorkSessions, shown inline on Browse for a leaf)
	/// keeps Worked by, Finished (which already carries the Active status pill), and exactly one
	/// action button per row -- Pause for an active session, Correct for a finished one -- and drops
	/// Started plus the backdate trigger, all of which stay one tap away via the row's own worker/
	/// session. The same columns and actions are present again on desktop.
	/// </summary>
	public async Task The_leaf_sessions_table_drops_to_a_single_action_button_on_a_phone_and_restores_the_rest_on_desktop()
	{
		var (leafId, _) = await fixture.SeedActiveSessionsAsync("Responsive session leaf", 1);

		await using var phoneContext = await fixture.NewContextAsync(SmallPhoneWidth, SmallPhoneHeight);
		var phone = await phoneContext.NewPageAsync();
		await SignInAsync(phone);
		await phone.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");

		var phoneRow = phone.Locator("tbody tr", new() { HasTextString = "Active Worker 1" }).First;
		(await phoneRow.GetByTitle("Pause job").IsVisibleAsync()).Should().BeTrue("Pause is the one row action that must stay reachable on a phone");
		(await phoneRow.Locator(".jt-session-started").IsVisibleAsync()).Should().BeFalse("Started is one tap away via the row's own session");
		(await phoneRow.GetByTitle("Correct").IsVisibleAsync()).Should().BeFalse("an active row keeps Pause, not Correct, as its one phone action");
		(await phoneRow.GetByTitle("Backdate finish").IsVisibleAsync()).Should()
			.BeFalse("the backdate trigger is one tap away via the row's own session");
		(await phoneRow.GetByText("Active", new() { Exact = true }).IsVisibleAsync()).Should()
			.BeTrue("Finished keeps the Active status pill on a phone");

		await using var desktopContext = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var desktop = await desktopContext.NewPageAsync();
		await SignInAsync(desktop);
		await desktop.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");

		var desktopRow = desktop.Locator("tbody tr", new() { HasTextString = "Active Worker 1" }).First;
		(await desktopRow.Locator(".jt-session-started").IsVisibleAsync()).Should().BeTrue("Started comes back when there is room for it");
		(await desktopRow.GetByTitle("Correct").IsVisibleAsync()).Should().BeTrue("Correct comes back when there is room for it");
	}

	[Fact]
	/// <summary>
	/// The sessions table's own Cost column (jt-col-cost, mirroring Browse's node table) narrows one
	/// step later than Started: at the tablet breakpoint Cost is visible and Started is not, and both
	/// arrive together only once the laptop breakpoint (d-lg) is reached.
	/// </summary>
	public async Task The_sessions_table_cost_column_outlasts_started_while_narrowing()
	{
		var (leafId, _) = await fixture.SeedActiveSessionsAsync("Cost column narrowing leaf", 1);

		await using var tabletContext = await fixture.NewContextAsync(TabletWidth, TabletHeight);
		var tablet = await tabletContext.NewPageAsync();
		await SignInAsync(tablet);
		await tablet.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");

		var tabletTable = tablet.Locator(".jt-table-block table");
		(await tabletTable.GetByRole(AriaRole.Columnheader, new() { Name = "Cost", Exact = true }).IsVisibleAsync()).Should()
			.BeTrue("Cost is visible at the tablet breakpoint");
		(await tabletTable.GetByRole(AriaRole.Columnheader, new() { Name = "Started", Exact = true }).IsVisibleAsync()).Should()
			.BeFalse("Started has already narrowed away at the tablet breakpoint");

		await using var laptopContext = await fixture.NewContextAsync(LaptopWidth, LaptopHeight);
		var laptop = await laptopContext.NewPageAsync();
		await SignInAsync(laptop);
		await laptop.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");

		var laptopTable = laptop.Locator(".jt-table-block table");
		(await laptopTable.GetByRole(AriaRole.Columnheader, new() { Name = "Cost", Exact = true }).IsVisibleAsync()).Should()
			.BeTrue("Cost stays visible at the laptop breakpoint");
		(await laptopTable.GetByRole(AriaRole.Columnheader, new() { Name = "Started", Exact = true }).IsVisibleAsync()).Should()
			.BeTrue("Started returns once the laptop breakpoint gives it room");
	}

	[Fact]
	/// <summary>
	/// A finished session has no Pause to fall back to, so on a phone its one surviving row action must
	/// be Correct, not nothing -- the opposite case from the active-session row above.
	/// </summary>
	public async Task The_leaf_sessions_table_keeps_correct_as_the_one_phone_action_for_a_finished_session()
	{
		var (leafId, _, _) = await fixture.SeedFinishedSessionAsync("Responsive finished session leaf");

		await using var phoneContext = await fixture.NewContextAsync(SmallPhoneWidth, SmallPhoneHeight);
		var phone = await phoneContext.NewPageAsync();
		await SignInAsync(phone);
		await phone.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={leafId.Value}");

		var phoneRow = phone.Locator("tbody tr").First;
		(await phoneRow.GetByTitle("Correct").IsVisibleAsync()).Should()
			.BeTrue("a finished row has no Pause, so Correct must be its one phone action");
		(await phoneRow.GetByTitle("Pause job").IsVisibleAsync()).Should().BeFalse("a finished session has nothing to pause");
	}

	[Theory]
	/// <summary>
	/// A page scrolls as one unit: nothing inside it is its own scrolling region. A nested scroller hides
	/// content behind a scrollbar a touch reader never sees, fights the page's own scroll gesture, clips
	/// any popover opened inside it, and pins a sticky table heading to the inner box rather than the
	/// viewport. Checked at the narrowest and widest viewports on a page carrying every table Browse can
	/// show — the subtree tree, and a leaf's own Sessions table.
	/// </summary>
	[MemberData(nameof(ViewportMatrix))]
	public async Task Nothing_on_the_browse_page_is_its_own_scrolling_region(int width, int height)
	{
		var branchId = await fixture.SeedBranchAsync("Scroll-region branch");
		_ = await fixture.SeedLeafAsync("Scroll-region first leaf", branchId);
		_ = await fixture.SeedLeafAsync("Scroll-region second leaf", branchId);

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse");

		var scrollers = await ScrollingRegionsAsync(page);

		scrollers.Should().BeEmpty($"the page should scroll as one unit at {width}x{height}, but these elements scroll on their own: " +
								   string.Join("; ", scrollers));
	}

	[Fact]
	/// <summary>
	/// The Start-for panel opened from the very last row of the subtree table floats over whatever
	/// follows the table (the Show archived control, the recently-visited list) rather than being clipped
	/// by an ancestor or painted over by it. Sampled by hit-testing the panel's own bottom edge: a
	/// clipped or under-painted panel does not answer at the point it appears to occupy.
	/// </summary>
	public async Task The_last_rows_start_for_panel_floats_clear_of_everything_below_the_table()
	{
		var branchId = await fixture.SeedBranchAsync("Popover branch");
		_ = await fixture.SeedLeafAsync("Popover first leaf", branchId);
		var lastLeafId = await fixture.SeedLeafAsync("Popover last leaf", branchId);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var lastRow = page.Locator("tbody tr", new() { HasTextString = "Popover last leaf" }).Last;
		var trigger = lastRow.GetByTitle("Start for…");
		await trigger.ScrollIntoViewIfNeededAsync();
		await trigger.ClickAsync();

		var panel = lastRow.Locator(".jt-backdate-panel--anchored");
		(await panel.IsVisibleAsync()).Should().BeTrue($"the Start-for panel for leaf {lastLeafId.Value} should open");

		// <details>'s own toggle event is queued, not dispatched synchronously from the click, so the
		// panel is briefly open but not yet pinned to the viewport by site.js -- hit-testing it in that
		// window measures the un-upgraded absolute position and fails for the wrong reason.
		await Assertions.Expect(panel).ToHaveCSSAsync("position", "fixed");

		var topmostAtBottomEdge = await TopmostAtAsync(panel, ".jt-backdate-panel", SamplePoint.BottomEdge);

		topmostAtBottomEdge.Should().Be(TopmostIsExpectedElement,
			"the panel's own bottom edge should be the topmost thing at that point — anything else means it is clipped or painted over");
	}

	[Fact]
	/// <summary>
	/// Every column heading paints above the rows beneath it, not behind them: the subtree's description
	/// cells are positioned (they draw the tree guides), which without an explicit stacking order lets
	/// row content paint over a heading that overlaps it.
	/// </summary>
	public async Task Every_subtree_column_heading_is_the_topmost_thing_at_its_own_position()
	{
		var branchId = await fixture.SeedBranchAsync("Heading order branch");
		_ = await fixture.SeedLeafAsync("Heading order leaf", branchId);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Browse?nodeId={branchId.Value}");

		var headings = page.Locator(".jt-browse-children-table thead th");
		var headingCount = await headings.CountAsync();
		headingCount.Should().BeGreaterThan(0);

		for (var index = 0; index < headingCount; ++index) {
			var heading = headings.Nth(index);
			if (!await heading.IsVisibleAsync()) {
				continue;
			}

			var topmost = await TopmostAtAsync(heading, "thead", SamplePoint.Centre);

			topmost.Should().Be(TopmostIsExpectedElement, $"column heading {index} should not be painted over by the table's own rows");
		}
	}

	/// <summary>
	///     Hit-tests one point of <paramref name="element" /> and reports what the browser says is topmost
	///     there: <see cref="TopmostIsExpectedElement" /> when it is the element itself (or anything inside
	///     <paramref name="expectedAncestorSelector" />), otherwise a description of whatever won instead.
	///     Everything happens in the page, because Playwright's bounding boxes and
	///     <c>document.elementFromPoint</c> do not share a coordinate space once the page has scrolled.
	/// </summary>
	private static async Task<string> TopmostAtAsync(ILocator element, string expectedAncestorSelector, SamplePoint point) =>
		await element.EvaluateAsync<string>(
			"""
			(element, [selector, sampleFromBottom, inset, expected]) => {
				element.scrollIntoView({ block: "center" });
				const rect = element.getBoundingClientRect();
				const x = rect.left + (rect.width / 2);
				const y = sampleFromBottom ? rect.bottom - inset : rect.top + (rect.height / 2);
				const hit = document.elementFromPoint(x, y);
				if (hit === null) {
					return `off-viewport y=${Math.round(y)} h=${window.innerHeight} ` +
						`scrollY=${Math.round(window.scrollY)}/${document.documentElement.scrollHeight - window.innerHeight}`;
				}

				return hit.closest(selector) !== null ? expected : hit.outerHTML.slice(0, 120);
			}
			""",
			new object[] { expectedAncestorSelector, point == SamplePoint.BottomEdge, PopoverEdgeSampleInset, TopmostIsExpectedElement });

	/// <summary>
	///     Every element whose computed overflow scrolls in either axis, described for a failure message.
	///     A declaration is enough to fail on: whether it happens to overflow right now depends on the
	///     data, and the rule is that no element may be a scrolling region at all.
	/// </summary>
	private static async Task<string[]> ScrollingRegionsAsync(IPage page) =>
		await page.EvaluateAsync<string[]>(
			"""
			() => Array.from(document.querySelectorAll("body *"))
				.filter(element => {
					const style = window.getComputedStyle(element);
					const scrolls = value => value === "auto" || value === "scroll";
					return scrolls(style.overflowX) || scrolls(style.overflowY);
				})
				.map(element => `${element.tagName.toLowerCase()}.${element.className || "(no class)"}`);
			""");

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
		for (var attempt = 0; attempt < maxTabs; ++attempt) {
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
