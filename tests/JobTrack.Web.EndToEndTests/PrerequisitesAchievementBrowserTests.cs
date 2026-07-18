namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for prerequisite editing and achievement updates (plan §8.5 slice 5):
///     adding a prerequisite is the representative round trip; the achievement page gets an
///     accessibility scan as a supplement to those checks, not a replacement.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class PrerequisitesAchievementBrowserTestsBase
{
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

	protected PrerequisitesAchievementBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_prerequisites_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		var nodeId = await fixture.SeedLeafAsync($"Overflow prerequisites leaf {width}x{height}");

		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Prerequisites?NodeId={nodeId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"the prerequisites page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_the_prerequisites_page_to_a_320px_wide_viewport_keeps_content_and_controls_usable()
	{
		var nodeId = await fixture.SeedLeafAsync("Reflow prerequisites leaf");

		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Prerequisites?NodeId={nodeId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.GetByRole(AriaRole.Button, new() { Name = "Search" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Adding_a_dependency_is_operable_by_keyboard_with_visible_focus()
	{
		const string RequiredJobDescription = "Keyboard prerequisite required job";
		_ = await fixture.SeedLeafAsync(RequiredJobDescription);
		var dependentJobId = await fixture.SeedLeafAsync("Keyboard prerequisite dependent job");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Prerequisites?NodeId={dependentJobId.Value}");

		await page.EvaluateAsync("document.body.focus()");
		await TabToAsync(page, "SearchText", 20);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		await page.Keyboard.TypeAsync(RequiredJobDescription);
		await page.Keyboard.PressAsync("Enter");

		await page.GetByRole(AriaRole.Radiogroup, new() { Name = $"Dependency for {RequiredJobDescription}" })
			.GetByRole(AriaRole.Radio, new() { Name = "Prerequisite for current" })
			.CheckAsync();
		await page.GetByRole(AriaRole.Button, new() { Name = "Add selected" }).ClickAsync();

		await page.WaitForSelectorAsync("text=Dependency added.");
	}

	[Fact]
	public async Task The_prerequisites_page_has_no_critical_or_serious_accessibility_violations()
	{
		var nodeId = await fixture.SeedLeafAsync("Accessibility prerequisites leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Prerequisites?NodeId={nodeId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Prerequisites");
	}

	[Fact]
	public async Task The_achievement_page_has_no_critical_or_serious_accessibility_violations()
	{
		var (leafId, _, _) = await fixture.SeedFinishedSessionAsync("Accessibility achievement leaf");

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Jobs/Achievement?JobNodeId={leafId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Jobs/Achievement");
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

public sealed class SqlitePrerequisitesAchievementBrowserTests : PrerequisitesAchievementBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqlitePrerequisitesAchievementBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlPrerequisitesAchievementBrowserTests : PrerequisitesAchievementBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlPrerequisitesAchievementBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
