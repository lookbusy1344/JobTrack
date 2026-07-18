namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using NodaTime;

/// <summary>
///     Real-browser evidence for personal schedule and exception management (plan §8.5 slice 6):
///     adding a schedule version is the representative round trip.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class ScheduleBrowserTestsBase
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

	protected ScheduleBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_schedule_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Rota/Index");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"the schedule page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_the_schedule_page_to_a_320px_wide_viewport_keeps_content_and_controls_usable()
	{
		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Rota/Index");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.GetByRole(AriaRole.Button, new() { Name = "Add rota version" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Adding_a_schedule_version_is_operable_by_keyboard_with_visible_focus()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Rota/Index");

		await page.EvaluateAsync("document.body.focus()");
		await TabToAsync(page, "VersionInput_EffectiveStart", 20);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		// Keyboard operability/focus-visibility is already evidenced by the Tab-to-field and
		// box-shadow assertions above. The values themselves are filled via Locator.FillAsync rather
		// than raw keystrokes into native type="date" inputs: those inputs consume digits per
		// locale-dependent segment (month/day/year) rather than as a literal "yyyy-MM-dd" string, so
		// typed keystrokes do not reliably reproduce the intended date. Bootstrap already provisions
		// the administrator with an open-ended default schedule version starting 2020-01-01
		// (EmployeeProvisioningDefaults.ScheduleEffectiveStart), so this version must be fully
		// bounded before that date to avoid the same-employee overlap constraint. This range (2011)
		// is kept distinct from SeedScheduleVersionAsync's own bounded range (2010) since both run
		// against the same shared BrowserFixture administrator within this test class.
		await page.Locator("#VersionInput_EffectiveStart").FillAsync("2011-01-01");
		await page.Locator("#VersionInput_EffectiveEnd").FillAsync("2011-06-01");
		await page.GetByRole(AriaRole.Button, new() { Name = "Add rota version" }).ClickAsync();

		await page.WaitForSelectorAsync("text=Rota version added.");
	}

	[Fact]
	public async Task The_schedule_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Rota/Index");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Rota/Index");
	}

	[Fact]
	public async Task The_correct_version_page_has_no_critical_or_serious_accessibility_violations()
	{
		var versionId = await fixture.SeedScheduleVersionAsync(new(2010, 1, 1));

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Rota/CorrectVersion?userId={fixture.AdministratorId.Value}&versionId={versionId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Rota/CorrectVersion");
	}

	[Fact]
	public async Task The_correct_exception_page_has_no_critical_or_serious_accessibility_violations()
	{
		var exceptionId = await fixture.SeedScheduleExceptionAsync(
			Instant.FromUtc(2026, 2, 1, 0, 0), Instant.FromUtc(2026, 2, 2, 0, 0));

		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync(
			$"{fixture.BaseAddress}/Rota/CorrectException?userId={fixture.AdministratorId.Value}&exceptionId={exceptionId.Value}");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Rota/CorrectException");
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

public sealed class SqliteScheduleBrowserTests : ScheduleBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteScheduleBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlScheduleBrowserTests : ScheduleBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlScheduleBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
