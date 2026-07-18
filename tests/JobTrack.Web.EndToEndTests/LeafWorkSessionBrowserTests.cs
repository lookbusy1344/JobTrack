namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for the leaf work / session workflow (plan §8.5 slice 4, fix-plan §2.5):
///     the one-click "Start work" composite (ADR 0038) is the representative round trip; the
///     correction page gets an accessibility scan as a supplement to those checks, not a replacement.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class LeafWorkSessionBrowserTestsBase
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

		(await page.GetByRole(AriaRole.Button, new() { Name = "Start work" }).IsVisibleAsync()).Should().BeTrue();
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
		await TabToButtonAsync(page, "Start work", 20);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		await page.Keyboard.PressAsync("Enter");
		await page.WaitForSelectorAsync("text=Work started.");

		(await page.Locator("text=Active").First.IsVisibleAsync()).Should().BeTrue();
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
