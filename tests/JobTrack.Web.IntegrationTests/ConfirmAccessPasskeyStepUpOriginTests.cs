namespace JobTrack.Web.IntegrationTests;

using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using TestSupport;
using Program = Program;

/// <summary>
///     ADR 0071 §8.3 / ADR 0057: a successful passkey step-up refreshes recent authentication while
///     leaving the absolute-session origin untouched. The WebAuthn crypto is the framework's concern,
///     proven end-to-end by the virtual-authenticator browser suite; here a decorator over the real
///     <see cref="PasskeyHandler{TUser}" /> stubs only the assertion verdict, so the production sign-in
///     funnel (<see cref="JobTrackSignInManager.SignInWithClaimsAsync" />) runs against a controllable
///     clock — the only way to reach the absolute ceiling, which the browser fixture's real clock cannot.
/// </summary>
public sealed partial class ConfirmAccessPasskeyStepUpOriginTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	private readonly MutableClock clock = new(Instant.FromUtc(2026, 8, 1, 9, 0, 0));
	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private PasskeyStepUpOriginWebApplicationFactory factory = null!;
	private static byte[] SeededCredentialId => [11, 22, 33, 44, 55, 66, 77, 88];

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		factory = new(database.ConnectionString, clock);
		client = factory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
	}

	public async Task DisposeAsync()
	{
		Dispose();
		await database.DisposeAsync();
	}

	public void Dispose()
	{
		client.Dispose();
		factory.Dispose();
	}

	[Fact]
	public async Task Passkey_step_up_refreshes_recent_authentication_and_preserves_the_absolute_origin()
	{
		var userId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.passkey.origin");
		await IdentityTestSupport.SeedSqlitePasskeyAsync(database.ConnectionString, userId, credentialId: SeededCredentialId);
		var authCookie = await SignInAsync("stepup.passkey.origin"); // origin = T0, recent = T0

		clock.Advance(Duration.FromMinutes(30)); // recent stale (> 15m), origin still fresh

		var stepUp = await PasskeyConfirmAsync(authCookie, "/Account/PersonalAccessTokens");
		var body = await stepUp.Content.ReadAsStringAsync();
		body.Should().Contain("redirect");
		body.Should().Contain("/Account/PersonalAccessTokens");
		body.Should().NotContain("error");

		// Carry the re-issued session cookie forward, as a browser would.
		var refreshed = WebTestHttp.FindSetCookie(stepUp, "Identity.Application");
		refreshed.Should().NotBeNull("the passkey step-up re-issued the session cookie");
		authCookie = WebTestHttp.ExtractCookiePair(refreshed!);

		// recent is refreshed: a sensitive action at T0 + 30m no longer demands step-up.
		var afterStepUp = await PostIssueAsync(authCookie);
		afterStepUp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		afterStepUp.Headers.Location!.OriginalString.Should().NotContain("/Account/ConfirmAccess");

		// origin is preserved at the original sign-in. Advancing a further 7h40m reaches T0 + 8h10m:
		// past the 8h ceiling measured from the T0 origin (session over), but only 7h40m past a
		// hypothetical origin reset to the T0 + 30m step-up (which would still be within the ceiling).
		// The Login redirect therefore proves the step-up left origin at T0.
		clock.Advance(Duration.FromHours(7) + Duration.FromMinutes(40));
		var pastCeiling = await GetPersonalAccessTokensPageAsync(authCookie);
		pastCeiling.StatusCode.Should().Be(HttpStatusCode.Redirect);
		pastCeiling.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	private async Task<HttpResponseMessage> PasskeyConfirmAsync(string authCookie, string returnUrl)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(authCookie, "/Account/ConfirmAccess");

		// The framework's PasskeySignInAsync needs the protected assertion-state cookie the options
		// handler sets, so generate options first (the real handler runs) and carry the state forward.
		using var optionsRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ConfirmAccess?handler=PasskeyOptions");
		optionsRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		optionsRequest.Headers.Add("X-CSRF-TOKEN", token);
		var optionsResponse = await client.SendAsync(optionsRequest);
		var cookies = string.Join("; ", new[] {
			authCookie, antiforgeryCookie,
		}.Concat(SetCookiePairs(optionsResponse)));

		using var request = new HttpRequestMessage(
			HttpMethod.Post, $"/Account/ConfirmAccess?handler=PasskeyConfirm&returnUrl={Uri.EscapeDataString(returnUrl)}");
		request.Headers.Add("Cookie", cookies);
		request.Headers.Add("X-CSRF-TOKEN", token);
		var encodedId = Base64Url.EncodeToString(SeededCredentialId);
		var assertionJson =
			"{\"id\":\"" + encodedId + "\",\"rawId\":\"" + encodedId + "\",\"type\":\"public-key\",\"clientExtensionResults\":{},"
			+ "\"response\":{\"clientDataJSON\":\"AAAA\",\"authenticatorData\":\"AAAA\",\"signature\":\"AAAA\"}}";
		request.Content = new StringContent(assertionJson, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	private static IEnumerable<string> SetCookiePairs(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var setCookies)
			? setCookies.Select(setCookie => setCookie.Split(';', 2)[0])
			: [];

	private async Task<HttpResponseMessage> GetPersonalAccessTokensPageAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostIssueAsync(string authCookie)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(authCookie, "/Account/PersonalAccessTokens");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/PersonalAccessTokens?handler=Issue");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Issue.Label"] = "laptop",
			["Issue.LifetimeDays"] = 30.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetAntiforgeryFormAsync(string? authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (!string.IsNullOrEmpty(authCookie)) {
			request.Headers.Add("Cookie", authCookie);
		}

		var response = await client.SendAsync(request);
		var responseBody = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(responseBody) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException($"No antiforgery token in {path} body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<string> SignInAsync(string userName)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(null, "/Account/Login");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = WebTestHttp.FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private sealed class MutableClock(Instant now) : IClock
	{
		private Instant _now = now;

		public Instant GetCurrentInstant() => _now;

		public void Advance(Duration duration) => _now += duration;
	}

	private sealed class PasskeyStepUpOriginWebApplicationFactory(string identityConnectionString, IClock clock)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("Authentication:Passkeys:Enabled", "true");
			_ = builder.UseSetting("Authentication:Passkeys:ServerDomain", "localhost");
			_ = builder.UseSetting("Authentication:Passkeys:Origins:0", "http://localhost");

			_ = builder.ConfigureTestServices(services => {
				services.RemoveAll<IClock>();
				_ = services.AddSingleton(clock);

				// Stub only the assertion verdict; the real handler still generates options/state and the
				// real sign-in funnel still stamps origin/recent, which is the behaviour under test.
				services.RemoveAll<IPasskeyHandler<JobTrackIdentityUser>>();
				_ = services.AddScoped<IPasskeyHandler<JobTrackIdentityUser>>(sp => new StubAssertionPasskeyHandler(
					ActivatorUtilities.CreateInstance<PasskeyHandler<JobTrackIdentityUser>>(sp),
					sp.GetRequiredService<UserManager<JobTrackIdentityUser>>()));
			});
		}
	}

	/// <summary>
	///     Decorates the real <see cref="PasskeyHandler{TUser}" />, delegating option generation and
	///     attestation unchanged, and stubbing <see cref="PerformAssertionAsync" /> to resolve the posted
	///     credential and report success — no WebAuthn signature check, so the test controls the verdict
	///     while every other part of the production sign-in path runs.
	/// </summary>
	private sealed class StubAssertionPasskeyHandler(
		PasskeyHandler<JobTrackIdentityUser> inner,
		UserManager<JobTrackIdentityUser> userManager) : IPasskeyHandler<JobTrackIdentityUser>
	{
		public Task<PasskeyCreationOptionsResult> MakeCreationOptionsAsync(PasskeyUserEntity user, HttpContext httpContext) =>
			inner.MakeCreationOptionsAsync(user, httpContext);

		public Task<PasskeyRequestOptionsResult> MakeRequestOptionsAsync(JobTrackIdentityUser? user, HttpContext httpContext) =>
			inner.MakeRequestOptionsAsync(user, httpContext);

		public Task<PasskeyAttestationResult> PerformAttestationAsync(PasskeyAttestationContext context) =>
			inner.PerformAttestationAsync(context);

		public async Task<PasskeyAssertionResult<JobTrackIdentityUser>> PerformAssertionAsync(PasskeyAssertionContext context)
		{
			if (!TryReadCredentialId(context.CredentialJson, out var credentialId)) {
				return PasskeyAssertionResult.Fail<JobTrackIdentityUser>(new("The credential id could not be read."));
			}

			var user = await userManager.FindByPasskeyIdAsync(credentialId);
			if (user is null) {
				return PasskeyAssertionResult.Fail<JobTrackIdentityUser>(new("No account owns that credential."));
			}

			var passkeys = await userManager.GetPasskeysAsync(user);
			var passkey = passkeys.FirstOrDefault(candidate => candidate.CredentialId.AsSpan().SequenceEqual(credentialId));
			return passkey is null
				? PasskeyAssertionResult.Fail<JobTrackIdentityUser>(new("The credential is not stored."))
				: PasskeyAssertionResult.Success(passkey, user);
		}

		private static bool TryReadCredentialId(string credentialJson, out byte[] credentialId)
		{
			credentialId = [];
			try {
				using var document = JsonDocument.Parse(credentialJson);
				if (!document.RootElement.TryGetProperty("id", out var idElement) || idElement.GetString() is not { Length: > 0 } encoded) {
					return false;
				}

				credentialId = Base64Url.DecodeFromChars(encoded);
				return credentialId.Length > 0;
			}
			catch (JsonException) {
				return false;
			}
			catch (FormatException) {
				return false;
			}
		}
	}
}
