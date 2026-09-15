namespace JobTrack.Web.EndToEndTests;

using System.Text;
using Abstractions;
using AwesomeAssertions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

/// <summary>
///     Stage 7 acceptance (ADR 0071 §8.7): the native ASP.NET Core passkey ceremonies driven end-to-end
///     over real Kestrel HTTPS by Chromium's virtual WebAuthn authenticator — enrol a discoverable
///     credential, sign in username-less with no TOTP step, and remove it. Handler substitutes cover the
///     error branches (integration suite); only a real authenticator is protocol evidence. Chromium only,
///     against both providers via the two concrete subclasses.
/// </summary>
public abstract class PasskeyBrowserTestsBase
{
	private const int DesktopWidth = 1280;
	private const int DesktopHeight = 800;

	private readonly BrowserFixture fixture;

	protected PasskeyBrowserTestsBase(BrowserFixture fixture) => this.fixture = fixture;

	[Fact]
	public async Task A_passkey_can_be_enrolled_and_appears_in_the_security_list()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		var authenticator = await PasskeyVirtualAuthenticator.AttachAsync(context, page);

		await SignInFreshWorkerAsync(page);
		await EnrolPasskeyAsync(page, "Virtual platform key");

		(await page.Locator("td", new() {
			HasTextString = "Virtual platform key",
		}).First.IsVisibleAsync()).Should().BeTrue();
		(await authenticator.CredentialCountAsync()).Should().Be(1, "the ceremony stored one discoverable credential on the authenticator");
	}

	[Fact]
	public async Task A_username_less_passkey_signs_in_from_the_login_page()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		_ = await PasskeyVirtualAuthenticator.AttachAsync(context, page);

		_ = await SignInFreshWorkerAsync(page);
		await EnrolPasskeyAsync(page, "Sign-in key");
		await SignOutAsync(page);

		await SignInWithPasskeyAsync(page);

		page.Url.Should().NotContain("/Account/Login", "the passkey assertion established a session");
	}

	[Fact]
	public async Task A_passkey_steps_up_an_existing_session()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		_ = await PasskeyVirtualAuthenticator.AttachAsync(context, page);

		_ = await SignInFreshWorkerAsync(page);
		await EnrolPasskeyAsync(page, "Step-up key");

		await page.GotoAsync($"{fixture.BaseAddress}/Account/ConfirmAccess?returnUrl=%2FAccount%2FPersonalAccessTokens");
		await page.Locator("[data-passkey-login-button]").ClickAsync();

		await page.WaitForURLAsync(url => url.Contains("/Account/PersonalAccessTokens", StringComparison.Ordinal), new() {
			Timeout = 20000,
		});
	}

	[Fact]
	public async Task A_successful_nonzero_counter_assertion_cannot_be_replayed()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		_ = await PasskeyVirtualAuthenticator.AttachAsync(context, page);
		_ = await SignInFreshWorkerAsync(page);
		await EnrolPasskeyAsync(page, "Replay-resistant key");
		await SignOutAsync(page);

		string? assertionJson = null;
		string? assertionCookies = null;
		string? antiforgeryToken = null;
		await page.RouteAsync("**/Account/Login?handler=PasskeySignIn", async route => {
			assertionJson = route.Request.PostData;
			var headers = await route.Request.AllHeadersAsync();
			assertionCookies = headers["cookie"];
			antiforgeryToken = headers["x-csrf-token"];
			await route.ContinueAsync();
		});
		await SignInWithPasskeyAsync(page);

		using var handler = new HttpClientHandler {
			ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
		};
		using var client = new HttpClient(handler);
		using var replay = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseAddress}/Account/Login?handler=PasskeySignIn");
		replay.Headers.Add("Cookie", assertionCookies ?? throw new InvalidOperationException("The assertion cookie was not captured."));
		replay.Headers.Add("X-CSRF-TOKEN", antiforgeryToken ?? throw new InvalidOperationException("The antiforgery token was not captured."));
		replay.Content = new StringContent(
			assertionJson ?? throw new InvalidOperationException("The assertion body was not captured."),
			Encoding.UTF8,
			"application/json");

		var response = await client.SendAsync(replay);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("error");
		_ = response.Headers.TryGetValues("Set-Cookie", out var setCookies);
		(setCookies ?? []).Should().NotContain(cookie => cookie.StartsWith("Identity.Application=", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_passkey_signs_in_without_a_totp_step_even_when_two_factor_is_enabled()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		_ = await PasskeyVirtualAuthenticator.AttachAsync(context, page);

		var workerId = await SignInFreshWorkerAsync(page);
		await EnrolPasskeyAsync(page, "Bypass key");
		await fixture.EnableTwoFactorAsync(workerId);
		await SignOutAsync(page);

		await SignInWithPasskeyAsync(page);

		page.Url.Should().NotContain("/Account/Login");
		page.Url.Should().NotContain("/Account/LoginTwoFactor",
			"the assurance table (ADR 0071 §1.3): a user-verified passkey establishes the session with no additional TOTP");
	}

	[Fact]
	public async Task A_removed_passkey_leaves_the_account_with_none()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		var authenticator = await PasskeyVirtualAuthenticator.AttachAsync(context, page);

		await SignInFreshWorkerAsync(page);
		await EnrolPasskeyAsync(page, "Doomed key");

		await page.Locator("button[title='Remove passkey']").First.ClickAsync();

		// The remove POST redirects back to /Account/Security (same URL), so wait on the empty-state copy
		// the reload renders rather than a URL change.
		await page.GetByText("You have no passkeys.").WaitForAsync(new() {
			Timeout = 15000,
		});
		// The server removed its row; the credential can linger on the device side (authenticator).
		_ = authenticator;
	}

	[Fact]
	public async Task The_security_page_has_no_critical_or_serious_accessibility_violations_with_a_passkey_listed()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		_ = await PasskeyVirtualAuthenticator.AttachAsync(context, page);

		await SignInFreshWorkerAsync(page);
		await EnrolPasskeyAsync(page, "Accessible key");

		var results = await page.RunAxe();
		BrowserTestSupport.AssertNoCriticalOrSeriousViolations(results, "/Account/Security");
	}

	[Fact]
	public async Task The_login_passkey_enhancement_runs_under_a_self_only_script_policy()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();

		var response = await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		var csp = response!.Headers.TryGetValue("content-security-policy", out var value) ? value : string.Empty;

		// passkeys.js is an external module loaded under script-src 'self'; the enhancement carries no
		// inline script or handlers, so the page needs no 'unsafe-inline' relaxation.
		csp.Should().Contain("script-src 'self'");
		csp.Should().NotContain("unsafe-inline");
		(await page.Locator("script[src*='js/passkeys']").CountAsync()).Should().Be(1);
		(await page.Locator("[data-passkey-login] script").CountAsync()).Should().Be(0, "no inline script drives the enhancement");
	}

	[Fact]
	public async Task With_javascript_disabled_the_password_form_still_works_and_the_passkey_enhancement_is_inert()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight, false);
		var page = await context.NewPageAsync();

		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		// The passkey button is present but its script never binds, so the password form is the whole story.
		await page.Locator("#Input_UserName").FillAsync(BrowserFixture.AdministratorUserName);
		await page.Locator("#Input_Password").FillAsync(BrowserFixture.AdministratorPassword);
		await page.Locator("[data-password-form] button[type=submit]").ClickAsync(new() {
			Force = true,
		});

		await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
		page.Url.Should().NotContain("/Account/Login", "the password form works with no JavaScript");
	}

	[Fact]
	public async Task The_explicit_passkey_button_displays_an_options_failure()
	{
		await using var context = await fixture.NewContextAsync(DesktopWidth, DesktopHeight);
		var page = await context.NewPageAsync();
		await page.RouteAsync("**/Account/Login?handler=PasskeyOptions", route => route.FulfillAsync(new() {
			Status = 200,
			ContentType = "application/json",
			Body = "{\"error\":\"Passkey sign-in expired; try again.\"}",
		}));

		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		await page.Locator("[data-passkey-login-button]").ClickAsync();

		var error = page.Locator("[data-passkey-login-error]");
		await error.WaitForAsync();
		(await error.TextContentAsync()).Should().Be("Passkey sign-in expired; try again.");
	}

	private async Task<AppUserId> SignInFreshWorkerAsync(IPage page)
	{
		var (workerId, userName) = await fixture.SeedBystanderWorkerAsync();
		await BrowserTestSupport.SignInAsync(page, fixture.BaseAddress, userName, BrowserFixture.AdministratorPassword);
		return workerId;
	}

	private async Task EnrolPasskeyAsync(IPage page, string name)
	{
		await page.GotoAsync($"{fixture.BaseAddress}/Account/Security");
		await page.Locator("#Add_Name").FillAsync(name);
		await page.Locator("[data-passkey-add-form] button[type=submit]").ClickAsync();

		// The Add POST re-renders the page with the ceremony options; passkeys.js runs
		// navigator.credentials.create on the virtual authenticator and posts the result, which redirects
		// back here with the success alert and the new row.
		await page.GetByText(name).First.WaitForAsync(new() {
			Timeout = 15000,
		});
	}

	private static async Task SignOutAsync(IPage page)
	{
		await page.Locator("form[action*='Logout'] button[type=submit], button:has-text('Sign out')").First.ClickAsync();
		await page.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.Ordinal), new() {
			Timeout = 15000,
		});
	}

	private async Task SignInWithPasskeyAsync(IPage page)
	{
		await page.GotoAsync($"{fixture.BaseAddress}/Account/Login");
		var button = page.Locator("[data-passkey-login-button]");
		try {
			await button.ClickAsync(new() {
				Timeout = 5000,
			});
		}
		catch (TimeoutException) {
			// Conditional-mediation autofill may have completed the assertion on load; the navigation
			// wait below still confirms the session.
		}

		await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal), new() {
			Timeout = 20000,
		});
	}
}

public sealed class SqlitePasskeyBrowserTests : PasskeyBrowserTestsBase, IClassFixture<PasskeySqliteBrowserFixture>
{
	public SqlitePasskeyBrowserTests(PasskeySqliteBrowserFixture fixture) : base(fixture) { }
}

public sealed class PostgreSqlPasskeyBrowserTests : PasskeyBrowserTestsBase, IClassFixture<PasskeyPostgreSqlBrowserFixture>
{
	public PostgreSqlPasskeyBrowserTests(PasskeyPostgreSqlBrowserFixture fixture) : base(fixture) { }
}
