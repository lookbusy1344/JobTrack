namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for the leaf work / session workflow (plan §8.5 slice 4, fix-plan §2.5):
///     the one-click "Start session" composite (ADR 0038) is the representative round trip; the
///     correction page gets an accessibility scan as a supplement to those checks, not a replacement.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class LeafWorkSessionBrowserTestsBase
{
	private const int RequiredSimultaneousWorkerCount = 3;

	// Representative viewport matrix (plan §8.5/§8.7, fix-plan §2.5): small phone, large phone,
	// tablet, and desktop -- matches JobBrowseBrowserTests' matrix.
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

	protected LeafWorkSessionBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_work_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		var leafId = await fixture.SeedLeafAsync($"Overflow work leaf {width}x{height}");

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"the work page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_the_work_page_to_a_320px_wide_viewport_keeps_content_and_controls_usable()
	{
		var leafId = await fixture.SeedLeafAsync("Reflow work leaf");

		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task The_work_page_honours_reduced_motion_preferences()
	{
		var leafId = await fixture.SeedLeafAsync("Reduced motion work leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		var mainAnimationName = await page.Locator("main[role=\"main\"]").EvaluateAsync<string>(
			"element => window.getComputedStyle(element).animationName");
		var buttonTransitionDuration = await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).EvaluateAsync<string>(
			"element => window.getComputedStyle(element).transitionDuration");

		mainAnimationName.Should().Be("none");
		buttonTransitionDuration.Split(',').Should().OnlyContain(duration => duration.Trim() == "0s");
	}

	[Fact]
	public async Task Starting_work_on_a_fresh_leaf_is_operable_by_keyboard_with_visible_focus()
	{
		var leafId = await fixture.SeedLeafAsync("Keyboard work session leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		await page.EvaluateAsync("document.body.focus()");
		await TabToButtonAsync(page, "Start session", 20);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		await page.Keyboard.PressAsync("Enter");
		await page.WaitForSelectorAsync("text=Session started.");

		(await page.Locator("text=Active").First.IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Start_for_native_disclosure_reveals_the_form_without_requiring_JavaScript()
	{
		var (leafId, _, _) = await fixture.SeedFinishedSessionAsync("Start-for disclosure leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		var disclosure = page.Locator("details.jt-start-for-disclosure");
		var trigger = disclosure.Locator("summary");
		(await disclosure.GetAttributeAsync("open")).Should().BeNull();

		await trigger.ClickAsync();

		(await disclosure.GetAttributeAsync("open")).Should().NotBeNull();
		(await disclosure.Locator("select[name=\"StartForUserId\"]").IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task The_sessions_summary_names_all_three_active_workers_and_remains_accessible()
	{
		var (leafId, workerNames) = await fixture.SeedActiveSessionsAsync(
			"Three active worker leaf", RequiredSimultaneousWorkerCount);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		(await page.GetByText($"{RequiredSimultaneousWorkerCount} active", new() { Exact = true }).IsVisibleAsync()).Should().BeTrue();
		foreach (var workerName in workerNames) {
			(await page.GetByText(workerName, new() { Exact = false }).First.IsVisibleAsync()).Should().BeTrue();
		}

		(await page.GetByText("more", new() { Exact = false }).CountAsync()).Should().Be(0, "the Sessions summary must not cap its worker list");
		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Work with three active workers");
	}

	[Fact]
	public async Task The_work_page_has_no_critical_or_serious_accessibility_violations()
	{
		var leafId = await fixture.SeedLeafAsync("Accessibility work leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Work");
	}

	[Fact]
	public async Task The_work_page_for_a_successful_leaf_has_no_critical_or_serious_accessibility_violations()
	{
		var leafId = await fixture.SeedSuccessLeafAsync("Accessibility success leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		(await page.Locator(".status-pill-closed").IsVisibleAsync()).Should().BeTrue();
		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Work for a successful leaf");
	}

	[Fact]
	public async Task The_work_page_for_an_archived_terminal_leaf_has_no_critical_or_serious_accessibility_violations()
	{
		var leafId = await fixture.SeedArchivedTerminalLeafAsync("Accessibility archived leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		(await page.GetByText("Archived", new() { Exact = false }).First.IsVisibleAsync()).Should().BeTrue();
		(await page.Locator("form[action*='handler=ReopenAndStart']").CountAsync()).Should()
			.Be(0, "an archived leaf cannot start a new session regardless of achievement");
		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Work for an archived terminal leaf");
	}

	[Fact]
	public async Task A_bystander_sees_no_reopen_form_on_a_terminal_leaf_they_do_not_control()
	{
		var leafId = await fixture.SeedSuccessLeafAsync("Bystander reopen leaf");
		var (_, bystanderUserName) = await fixture.SeedBystanderWorkerAsync();

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		await page.Locator("#Input_UserName").FillAsync(bystanderUserName);
		await page.Locator("#Input_Password").FillAsync(BrowserFixture.AdministratorPassword);
		await page.Locator("button[type=submit]").ClickAsync();
		await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		(await page.GetByText("Completed.", new() { Exact = false }).IsVisibleAsync()).Should().BeTrue();
		(await page.Locator("form[action*='handler=ReopenAndStart']").CountAsync()).Should()
			.Be(0, "a worker with no ownership or management capability must not see a reopen form (rendering hint follows server authorization)");
		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Work for a bystander viewing a terminal leaf");
	}

	[Fact]
	public async Task The_full_leaf_workflow_is_repeatable_by_keyboard_and_remains_accessible()
	{
		var leafId = await fixture.SeedLeafAsync("Golden leaf workflow");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		await page.GetByRole(AriaRole.Button, new() { Name = "Start session", Exact = true }).PressAsync("Enter");
		await page.GetByText("Session started.", new() { Exact = true }).WaitForAsync();

		await page.Locator("#end-session")
			.GetByRole(AriaRole.Button, new() { Name = "Pause work", Exact = true }).PressAsync("Enter");
		await page.GetByText("Ends this session; the job stays In Progress.", new() { Exact = true }).WaitForAsync();

		await page.GetByRole(AriaRole.Button, new() { Name = "Mark complete", Exact = true }).PressAsync("Enter");
		await page.GetByText("Job completed.", new() { Exact = true }).WaitForAsync();

		var reason = page.Locator("#reopenReason");
		await reason.FocusAsync();
		(await page.EvaluateAsync<string>("document.activeElement.id")).Should().Be("reopenReason");
		await reason.FillAsync("More work was found");
		await reason.PressAsync("Enter");

		await page.GetByText("Job reopened. Session started.", new() { Exact = true }).WaitForAsync();
		(await page.GetByText("In Progress", new() { Exact = true }).First.IsVisibleAsync()).Should().BeTrue();
		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Work after the complete reopen workflow");
	}

	[Fact]
	public async Task Multi_worker_completion_finishes_every_worker_then_completes_the_job()
	{
		var (leafId, _) = await fixture.SeedActiveSessionsAsync(
			"Golden multi-worker completion", RequiredSimultaneousWorkerCount);

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Work?LeafNodeId={leafId.Value}");

		(await page.Locator("div.jt-completion-review").IsVisibleAsync()).Should().BeTrue();

		await page.GetByRole(
			AriaRole.Button,
			new() { Name = $"Finish {RequiredSimultaneousWorkerCount} sessions and complete job", Exact = true }).PressAsync("Enter");

		await page.GetByText(
			$"Job completed and {RequiredSimultaneousWorkerCount} sessions finished.",
			new() { Exact = true }).WaitForAsync();
		(await page.GetByText("Closed", new() { Exact = true }).IsVisibleAsync()).Should().BeTrue();
		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Work after multi-worker completion");
	}

	[Fact]
	public async Task The_correct_session_page_has_no_critical_or_serious_accessibility_violations()
	{
		var (leafId, sessionId, _) = await fixture.SeedFinishedSessionAsync("Accessibility correct-session leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync(
			$"{fixture.BaseAddress}/Jobs/CorrectSession?LeafNodeId={leafId.Value}&WorkedByUserId={fixture.AdministratorId.Value}&SessionId={sessionId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/CorrectSession");
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

	private static async Task TabToButtonAsync(IPage page, string buttonText, int maxTabs)
	{
		for (var attempt = 0; attempt < maxTabs; attempt++) {
			await page.Keyboard.PressAsync("Tab");
			var isMatch = await page.EvaluateAsync<bool>(
				"text => document.activeElement.tagName === 'BUTTON' && document.activeElement.textContent.trim() === text",
				buttonText);
			if (isMatch) {
				return;
			}
		}

		throw new InvalidOperationException($"Tabbing {maxTabs} times from the page load never reached the '{buttonText}' button.");
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

public sealed class SqliteLeafWorkSessionBrowserTests : LeafWorkSessionBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteLeafWorkSessionBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlLeafWorkSessionBrowserTests : LeafWorkSessionBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlLeafWorkSessionBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
