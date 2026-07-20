namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using NodaTime;

/// <summary>
///     Real-browser evidence for authorized employee/schedule/rate administration (plan §8.5 slice 7,
///     rate side): adding a user cost rate is the representative round trip.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class RateAdministrationBrowserTestsBase
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

	protected RateAdministrationBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_rates_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/Rates?UserId={fixture.AdministratorId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"the rates page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_the_rates_page_to_a_320px_wide_viewport_keeps_content_and_controls_usable()
	{
		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/Rates?UserId={fixture.AdministratorId.Value}");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.GetByRole(AriaRole.Button, new() { Name = "Add user cost rate" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_is_operable_by_keyboard_with_visible_focus()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/Rates?UserId={fixture.AdministratorId.Value}");

		await page.EvaluateAsync("document.body.focus()");
		await TabToAsync(page, "UserCostRateInput_AmountPerHour", 20);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		await page.Keyboard.TypeAsync("42.50");
		await page.Keyboard.PressAsync("Tab");
		// A native datetime-local control's keyboard segment order (month/day/year/hour/minute/meridiem
		// vs. year-first) is locale- and browser-dependent, so a blind digit sequence isn't a reliable
		// way to assert a specific value -- fill the now-blank field directly instead (§2.4: no more
		// DateTimeOffset default silently pre-filling a bogus "0001-01-01T00:00" time to type over).
		await page.Locator("#UserCostRateInput_EffectiveStart").FillAsync("2026-01-01T00:00");
		await page.Keyboard.PressAsync("Enter");

		await page.WaitForSelectorAsync("text=User cost rate added.");
	}

	[Fact]
	public async Task The_rates_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/Rates?UserId={fixture.AdministratorId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Admin/Rates");
	}

	[Fact]
	public async Task The_correct_user_cost_rate_page_has_no_critical_or_serious_accessibility_violations()
	{
		var rateId = await fixture.SeedUserCostRateAsync(Instant.FromUtc(2010, 1, 1, 0, 0));

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/CorrectUserCostRate?userId={fixture.AdministratorId.Value}&rateId={rateId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Admin/CorrectUserCostRate");
	}

	[Fact]
	public async Task The_correct_node_rate_override_page_has_no_critical_or_serious_accessibility_violations()
	{
		var overrideId = await fixture.SeedNodeRateOverrideAsync(Instant.FromUtc(2026, 1, 1, 0, 0));

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync(
			$"{fixture.BaseAddress}/Admin/CorrectNodeRateOverride?userId={fixture.AdministratorId.Value}&overrideId={overrideId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Admin/CorrectNodeRateOverride");
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

public sealed class SqliteRateAdministrationBrowserTests : RateAdministrationBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteRateAdministrationBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlRateAdministrationBrowserTests : RateAdministrationBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlRateAdministrationBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
