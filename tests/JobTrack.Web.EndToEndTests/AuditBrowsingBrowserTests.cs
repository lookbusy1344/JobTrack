namespace JobTrack.Web.EndToEndTests;

using System.Globalization;
using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for audit browsing with permission-sensitive detail (plan §8.5 slice 9):
///     searching audit history is the representative round trip.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class AuditBrowsingBrowserTestsBase
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

	protected AuditBrowsingBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_audit_history_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Audit/Index");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"the audit history page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_the_audit_history_page_to_a_320px_wide_viewport_keeps_content_and_controls_usable()
	{
		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Audit/Index");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.GetByRole(AriaRole.Button, new() { Name = "Search" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Searching_audit_history_by_actor_is_operable_by_keyboard_with_visible_focus()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Audit/Index");

		await page.EvaluateAsync("document.body.focus()");
		await TabToAsync(page, "ActorId", 20);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		await page.Keyboard.TypeAsync(fixture.AdministratorId.Value.ToString(CultureInfo.InvariantCulture));
		await page.Keyboard.PressAsync("Enter");

		await page.WaitForURLAsync(url => url.Contains("ActorId=", StringComparison.Ordinal));
		var hasResultsOrEmptyMessage =
			await page.Locator("table.table").Or(page.Locator("text=No matching audit events.")).First.IsVisibleAsync();
		hasResultsOrEmptyMessage.Should().BeTrue();
	}

	[Fact]
	public async Task The_audit_history_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Audit/Index");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Audit/Index");
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

public sealed class SqliteAuditBrowsingBrowserTests : AuditBrowsingBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteAuditBrowsingBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlAuditBrowsingBrowserTests : AuditBrowsingBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlAuditBrowsingBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
