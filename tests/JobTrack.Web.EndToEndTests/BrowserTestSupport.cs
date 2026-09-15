namespace JobTrack.Web.EndToEndTests;

using AwesomeAssertions;
using Deque.AxeCore.Commons;
using Microsoft.Playwright;

internal static class BrowserTestSupport
{
	public static Task SignInAdministratorAsync(IPage page, string baseAddress) =>
		SignInAsync(page, baseAddress, BrowserFixture.AdministratorUserName, BrowserFixture.AdministratorPassword);

	public static async Task SignInAsync(IPage page, string baseAddress, string userName, string password)
	{
		await page.GotoAsync($"{baseAddress}/Account/Login");
		await page.Locator("#Input_UserName").FillAsync(userName);
		await page.Locator("#Input_Password").FillAsync(password);
		await page.Locator("[data-password-form] button[type=submit]").ClickAsync();
		await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
	}

	public static void AssertNoCriticalOrSeriousViolations(AxeResult results, string pageName)
	{
		var criticalOrSerious = results.Violations
									   .Where(violation => violation.Impact is "critical" or "serious")
									   .ToArray();

		criticalOrSerious.Should().BeEmpty(
			$"{pageName} should have no critical/serious accessibility violations, found: " +
			string.Join("; ", criticalOrSerious.Select(violation => $"{violation.Id} ({violation.Impact}): {violation.Help}")));
	}
}
