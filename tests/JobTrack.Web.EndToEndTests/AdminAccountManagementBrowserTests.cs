namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Real-browser evidence for administrator account provisioning, disablement, reset, and role
///     assignment (plan §8.5 slice 10): creating an employee account is the representative round trip;
///     the role-assignment page gets an accessibility scan as a supplement to those checks, not a
///     replacement.
/// </summary>
/// <remarks>
///     Requires <c>playwright install chromium</c> to have been run once outside this repo's usual
///     <c>dotnet restore</c>/<c>dotnet build</c> -- see <c>docs/operations/browser-testing.md</c>.
/// </remarks>
public abstract class AdminAccountManagementBrowserTestsBase
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

	protected AdminAccountManagementBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	public static TheoryData<int, int> ViewportMatrix => new() {
		{ SmallPhoneWidth, SmallPhoneHeight }, { LargePhoneWidth, LargePhoneHeight }, { TabletWidth, TabletHeight }, { DesktopWidth, DesktopHeight },
	};

	[Theory]
	[MemberData(nameof(ViewportMatrix))]
	public async Task The_manage_employee_account_page_has_no_unintended_horizontal_overflow(int width, int height)
	{
		await using var context = await fixture.NewContextAsync(width, height);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/ManageEmployeeAccount");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

		scrollWidth.Should()
			.BeLessThanOrEqualTo(clientWidth, $"the manage employee account page should not overflow horizontally at {width}x{height}");
	}

	[Fact]
	public async Task Reflowing_the_manage_employee_account_page_to_a_320px_wide_viewport_keeps_content_and_controls_usable()
	{
		await using var context = await fixture.NewContextAsync(ReflowWidth, ReflowHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/ManageEmployeeAccount");

		var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
		var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
		scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, "WCAG 1.4.10 Reflow requires no horizontal scrolling at a 320 CSS px viewport");

		(await page.GetByRole(AriaRole.Button, new() { Name = "Create" }).IsVisibleAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task Creating_an_employee_account_is_operable_by_keyboard_with_visible_focus()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/ManageEmployeeAccount");

		await page.EvaluateAsync("document.body.focus()");
		await TabToAsync(page, "CreateEmployee_DisplayName", 20);

		var focusBoxShadow = await page.EvaluateAsync<string>("window.getComputedStyle(document.activeElement).boxShadow");
		focusBoxShadow.Should().NotBe("none", "a keyboard-focused control must have a visible focus indicator (plan §8.5 keyboard evidence)");

		await page.Keyboard.TypeAsync("Keyboard-created Employee");
		await page.Keyboard.PressAsync("Tab");
		await page.Keyboard.TypeAsync("Etc/UTC");
		await page.Keyboard.PressAsync("Tab");
		// DefaultHourlyRate sits between IanaTimeZone and UserName and already has a valid default
		// value (CreateEmployeeInput.DefaultHourlyRateAmount), so it's tabbed past rather than typed into.
		await page.Keyboard.PressAsync("Tab");
		await page.Keyboard.TypeAsync($"keyboard.employee.{Guid.NewGuid():N}");
		await page.Keyboard.PressAsync("Tab");
		await page.Keyboard.TypeAsync("Correct-Horse-Battery-42!");
		await page.Keyboard.PressAsync("Enter");

		await page.WaitForSelectorAsync("text=created");
	}

	[Fact]
	public async Task The_manage_employee_account_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/ManageEmployeeAccount");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Admin/ManageEmployeeAccount");
	}

	[Fact]
	public async Task The_assign_role_page_has_no_critical_or_serious_accessibility_violations()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		await SignInAsync(page);
		await page.GotoAsync($"{fixture.BaseAddress}/Admin/AssignRole");

		AssertNoCriticalOrSeriousViolations(await page.RunAxe(), "/Admin/AssignRole");
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

public sealed class SqliteAdminAccountManagementBrowserTests : AdminAccountManagementBrowserTestsBase, IClassFixture<SqliteBrowserFixture>
{
	public SqliteAdminAccountManagementBrowserTests(SqliteBrowserFixture fixture) : base(fixture)
	{
	}
}

public sealed class PostgreSqlAdminAccountManagementBrowserTests : AdminAccountManagementBrowserTestsBase, IClassFixture<PostgreSqlBrowserFixture>
{
	public PostgreSqlAdminAccountManagementBrowserTests(PostgreSqlBrowserFixture fixture) : base(fixture)
	{
	}
}
