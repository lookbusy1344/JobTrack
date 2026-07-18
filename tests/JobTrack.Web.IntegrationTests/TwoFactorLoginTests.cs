namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Abstractions;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TestSupport;
using Program = Program;

/// <summary>
///     ADR 0037: the two-factor challenge step of sign-in, exercised over real HTTP against a
///     schema-deployed SQLite database, mirroring <see cref="AccountFlowTests" />'s direct-request style.
///     The TOTP code generator here reimplements RFC 6238 independently of
///     <c>AuthenticatorTokenProvider&lt;TUser&gt;</c> so the test is an independent proof the framework's
///     own verification actually accepts a real authenticator app's code, not a tautology against the
///     same code path.
/// </summary>
public sealed partial class TwoFactorLoginTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AuthenticatorKeyProtectionPurpose = "JobTrack.Identity.AuthenticatorKey.v1";
	private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
	private const int TotpStepSeconds = 30;
	private const int TotpDigits = 6;

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		factory = new(database.ConnectionString);
		client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
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
	public async Task Signing_in_with_a_correct_password_on_a_two_factor_account_challenges_for_a_code_instead_of_authenticating()
	{
		await SeedUserWithTwoFactorAsync("ada.2fa", "JBSWY3DPEHPK3PXP");

		var response = await PostLoginAsync("ada.2fa", KnownPassword);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/LoginTwoFactor");
		FindSetCookie(response, "Identity.Application").Should().BeNull("the session must not be established before the code step");
	}

	[Fact]
	public async Task Submitting_a_valid_totp_code_completes_sign_in()
	{
		const string secret = "JBSWY3DPEHPK3PXP";
		var appUserId = await SeedUserWithTwoFactorAsync("grace.2fa", secret);

		var loginResponse = await PostLoginAsync("grace.2fa", KnownPassword);
		var twoFactorUserIdCookie = ExtractCookiePair(
			FindSetCookie(loginResponse, "TwoFactorUserId") ?? throw new InvalidOperationException("No two-factor user id cookie set."));

		var code = GenerateTotpCode(secret, DateTimeOffset.UtcNow);
		var response = await PostTwoFactorCodeAsync(twoFactorUserIdCookie, code);
		var auditOperation = await GetLatestAuditOperationAsync(appUserId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var authCookie = FindSetCookie(response, "Identity.Application");
		authCookie.Should().NotBeNull();
		auditOperation.Should().Be("authentication.login-success");
	}

	[Fact]
	public async Task Submitting_an_incorrect_totp_code_does_not_complete_sign_in()
	{
		const string secret = "JBSWY3DPEHPK3PXP";
		var appUserId = await SeedUserWithTwoFactorAsync("kat.2fa", secret);

		var loginResponse = await PostLoginAsync("kat.2fa", KnownPassword);
		var twoFactorUserIdCookie = ExtractCookiePair(
			FindSetCookie(loginResponse, "TwoFactorUserId") ?? throw new InvalidOperationException("No two-factor user id cookie set."));

		var response = await PostTwoFactorCodeAsync(twoFactorUserIdCookie, "000000");
		var auditOperation = await GetLatestAuditOperationAsync(appUserId);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		FindSetCookie(response, "Identity.Application").Should().BeNull();
		auditOperation.Should().Be("authentication.two-factor-failed");
	}

	[Fact]
	public async Task Loading_the_login_form_does_not_spend_the_credential_attempt_rate_limit_before_two_factor()
	{
		ReplaceHost(1);
		await SeedUserWithTwoFactorAsync("hedy.2fa", "JBSWY3DPEHPK3PXP");

		var loginResponse = await PostLoginAsync("hedy.2fa", KnownPassword);

		loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		loginResponse.Headers.Location!.OriginalString.Should().Contain("/Account/LoginTwoFactor");
	}

	[Fact]
	public async Task A_rate_limited_login_attempt_returns_the_html_login_page()
	{
		ReplaceHost(1);
		await SeedUserWithTwoFactorAsync("dorothy.2fa", "JBSWY3DPEHPK3PXP");

		_ = await PostLoginAsync("dorothy.2fa", "wrong-password");
		var response = await PostLoginAsync("dorothy.2fa", "wrong-password");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
		response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
		body.Should().Contain("Too many sign-in attempts");
		body.Should().Contain("<form");
	}

	[Fact]
	public async Task A_failed_two_factor_attempt_for_one_user_does_not_consume_another_users_sign_in_budget()
	{
		ReplaceHost(1);
		const string firstSecret = "JBSWY3DPEHPK3PXP";
		const string secondSecret = "KRUGS4ZANFZSAYJA";
		await SeedUserWithTwoFactorAsync("heidi.2fa", firstSecret);
		await SeedUserWithTwoFactorAsync("irene.2fa", secondSecret);

		var firstLogin = await PostLoginAsync("heidi.2fa", KnownPassword);
		var firstTwoFactorUserIdCookie = ExtractCookiePair(
			FindSetCookie(firstLogin, "TwoFactorUserId") ??
			throw new InvalidOperationException("No two-factor user id cookie set for the first account."));
		var secondLogin = await PostLoginAsync("irene.2fa", KnownPassword);
		var secondTwoFactorUserIdCookie = ExtractCookiePair(
			FindSetCookie(secondLogin, "TwoFactorUserId") ??
			throw new InvalidOperationException("No two-factor user id cookie set for the second account."));

		var failedTwoFactor = await PostTwoFactorCodeAsync(secondTwoFactorUserIdCookie, "000000");
		var firstCode = GenerateTotpCode(firstSecret, DateTimeOffset.UtcNow);
		var succeededTwoFactor = await PostTwoFactorCodeAsync(firstTwoFactorUserIdCookie, firstCode);

		firstLogin.StatusCode.Should().Be(HttpStatusCode.Redirect);
		secondLogin.StatusCode.Should().Be(HttpStatusCode.Redirect);
		failedTwoFactor.StatusCode.Should().Be(HttpStatusCode.OK);
		var failedTwoFactorBody = await failedTwoFactor.Content.ReadAsStringAsync();
		failedTwoFactorBody.Should().Contain("The verification code is incorrect.");
		succeededTwoFactor.StatusCode.Should().Be(HttpStatusCode.Redirect);
		FindSetCookie(succeededTwoFactor, "Identity.Application").Should().NotBeNull();
	}

	private async Task<HttpResponseMessage> PostLoginAsync(string userName, string password)
	{
		var (antiforgeryCookie, token) = await GetFormAsync("/Account/Login");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostTwoFactorCodeAsync(string twoFactorUserIdCookie, string code)
	{
		var (antiforgeryCookie, token) = await GetFormAsync("/Account/LoginTwoFactor", twoFactorUserIdCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/LoginTwoFactor");
		request.Headers.Add("Cookie", $"{twoFactorUserIdCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.Code"] = code,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string path, string? extraCookie = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (extraCookie is not null) {
			request.Headers.Add("Cookie", extraCookie);
		}

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException($"No antiforgery token in {path} body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	private async Task<string> GetLatestAuditOperationAsync(AppUserId actorUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT operation
							  FROM audit_event
							  WHERE actor_user_id = $actorUserId
							  ORDER BY id DESC
							  LIMIT 1;
							  """;
		_ = command.Parameters.AddWithValue("$actorUserId", actorUserId.Value);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private static string GenerateTotpCode(string base32Secret, DateTimeOffset timestamp)
	{
		var key = Base32Decode(base32Secret);
		var counter = (long)(timestamp - DateTimeOffset.UnixEpoch).TotalSeconds / TotpStepSeconds;
		var counterBytes = BitConverter.GetBytes(counter);
		if (BitConverter.IsLittleEndian) {
			Array.Reverse(counterBytes);
		}

		// HMAC-SHA1 is RFC 6238's mandated TOTP algorithm, not a discretionary weak-crypto choice --
		// the same algorithm AuthenticatorTokenProvider<TUser> uses internally to verify the code.
#pragma warning disable CA5350
		using var hmac = new HMACSHA1(key);
#pragma warning restore CA5350
		var hash = hmac.ComputeHash(counterBytes);
		var offset = hash[^1] & 0x0F;
		var binaryCode =
			((hash[offset] & 0x7F) << 24) |
			((hash[offset + 1] & 0xFF) << 16) |
			((hash[offset + 2] & 0xFF) << 8) |
			(hash[offset + 3] & 0xFF);
		var truncated = binaryCode % (int)Math.Pow(10, TotpDigits);

		return truncated.ToString(CultureInfo.InvariantCulture).PadLeft(TotpDigits, '0');
	}

	private static byte[] Base32Decode(string base32)
	{
		var trimmed = base32.TrimEnd('=').ToUpperInvariant();
		var output = new List<byte>();
		var bitBuffer = 0;
		var bitCount = 0;

		foreach (var c in trimmed) {
			bitBuffer = (bitBuffer << 5) | Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
			bitCount += 5;
			if (bitCount >= 8) {
				output.Add((byte)((bitBuffer >> (bitCount - 8)) & 0xFF));
				bitCount -= 8;
			}
		}

		return [.. output];
	}

	private void ReplaceHost(int loginRateLimitPermitLimit)
	{
		client.Dispose();
		factory.Dispose();

		factory = new(database.ConnectionString, loginRateLimitPermitLimit);
		client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
	}

	private async Task<AppUserId> SeedUserWithTwoFactorAsync(string userName, string base32Secret)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		var appUserId = (long)(await insertAppUser.ExecuteScalarAsync())!;

		var placeholderUser = new JobTrackIdentityUser {
			AppUserId = new(appUserId),
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = string.Empty,
			SecurityStamp = Guid.NewGuid().ToString(),
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(placeholderUser, KnownPassword);

		// Encrypted with the same purpose string JobTrackUserStore uses (ADR 0037) — the app's own
		// Data Protection key ring, resolved from the running host, so the store can decrypt it.
		var dataProtectionProvider = factory.Services.GetRequiredService<IDataProtectionProvider>();
		var protector = dataProtectionProvider.CreateProtector(AuthenticatorKeyProtectionPurpose);
		var protectedKey = protector.Protect(Encoding.UTF8.GetBytes(base32Secret));

		await using var insertIdentityUser = connection.CreateCommand();
		insertIdentityUser.CommandText = """
										 INSERT INTO identity_user
										 	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										 	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count,
										 	 two_factor_enabled, authenticator_key_protected)
										 VALUES
										 	($appUserId, $userName, $normalizedUserName, $passwordHash, $securityStamp,
										 	 $concurrencyStamp, 0, 1, 1, 0, 1, $authenticatorKeyProtected);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", placeholderUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$authenticatorKeyProtected", protectedKey);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();

		return new(appUserId);
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private sealed class TestWebApplicationFactory(
		string identityConnectionString,
		int? loginRateLimitPermitLimit = null) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			if (loginRateLimitPermitLimit is not null) {
				_ = builder.UseSetting("RateLimiting:LoginPermitLimit", loginRateLimitPermitLimit.Value.ToString(CultureInfo.InvariantCulture));
			}
		}
	}
}
