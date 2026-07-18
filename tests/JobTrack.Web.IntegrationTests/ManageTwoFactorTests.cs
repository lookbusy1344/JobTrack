namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Abstractions;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Self-service TOTP enrolment and disablement (ADR 0037), exercised over real HTTP against a
///     schema-deployed SQLite database — mirroring <see cref="AccountFlowTests" />'s direct-request
///     style. Reuses <see cref="TwoFactorLoginTests" />'s independent RFC 6238 code generator.
/// </summary>
public sealed partial class ManageTwoFactorTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
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
	public async Task Confirming_a_valid_code_enables_two_factor()
	{
		var appUserId = await SeedUserAsync("ada.enrol");
		var authCookie = await SignInAsync("ada.enrol");
		var originalSecurityStamp = await GetSecurityStampAsync(appUserId);
		var seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = new(appUserId), CorrelationId = Guid.NewGuid() },
			TargetUserId = new(appUserId),
			Label = "enable-audit-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var (secret, refreshedAuthCookie, antiforgeryCookie, token) = await GetEnrolmentFormAsync(authCookie);
		var code = GenerateTotpCode(secret, DateTimeOffset.UtcNow);

		var response = await PostConfirmAsync(refreshedAuthCookie, antiforgeryCookie, token, code);
		var auditOperation = await GetLatestAuditOperationAsync(new(appUserId));
		using var apiRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/employees/{appUserId}/rates");
		apiRequest.Headers.Authorization = new("Bearer", issued.Token);
		var apiResponse = await client.SendAsync(apiRequest);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var updatedSecurityStamp = await GetSecurityStampAsync(appUserId);
		var (enabled, keyProtected) = await GetTwoFactorStateAsync(appUserId);
		enabled.Should().BeTrue();
		keyProtected.Should().NotBeNull();
		updatedSecurityStamp.Should().NotBe(originalSecurityStamp);
		auditOperation.Should().Be("authentication.two-factor-enabled");
		apiResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Rendering_the_enrolment_page_does_not_rotate_the_security_stamp()
	{
		var appUserId = await SeedUserAsync("no.rotate");
		var authCookie = await SignInAsync("no.rotate");
		var originalSecurityStamp = await GetSecurityStampAsync(appUserId);

		_ = await GetEnrolmentFormAsync(authCookie);

		var updatedSecurityStamp = await GetSecurityStampAsync(appUserId);
		updatedSecurityStamp.Should().Be(originalSecurityStamp);
	}

	[Fact]
	public async Task Confirming_an_incorrect_code_does_not_enable_two_factor()
	{
		var appUserId = await SeedUserAsync("grace.enrol");
		var authCookie = await SignInAsync("grace.enrol");

		var (_, refreshedAuthCookie, antiforgeryCookie, token) = await GetEnrolmentFormAsync(authCookie);

		var response = await PostConfirmAsync(refreshedAuthCookie, antiforgeryCookie, token, "000000");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var (enabled, _) = await GetTwoFactorStateAsync(appUserId);
		enabled.Should().BeFalse();
	}

	[Fact]
	public async Task Disabling_with_the_correct_password_clears_two_factor_state()
	{
		var appUserId = await SeedUserAsync("kat.disable");
		var authCookie = await SignInAsync("kat.disable");
		await SeedTwoFactorEnabledAsync(appUserId);

		var (antiforgeryCookie, token) = await GetFormAsync("/Account/ManageTwoFactor", authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/ManageTwoFactor?handler=Disable");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Disable.CurrentPassword"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var (enabled, keyProtected) = await GetTwoFactorStateAsync(appUserId);
		enabled.Should().BeFalse();
		keyProtected.Should().BeNull();
	}

	[Fact]
	public async Task Disabling_two_factor_revokes_the_users_personal_access_tokens()
	{
		var appUserId = await SeedUserAsync("mae.disable");
		var authCookie = await SignInAsync("mae.disable");
		await SeedTwoFactorEnabledAsync(appUserId);
		var originalSecurityStamp = await GetSecurityStampAsync(appUserId);
		var seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = new(appUserId), CorrelationId = Guid.NewGuid() },
			TargetUserId = new(appUserId),
			Label = "disable-audit-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var (antiforgeryCookie, token) = await GetFormAsync("/Account/ManageTwoFactor", authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/ManageTwoFactor?handler=Disable");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Disable.CurrentPassword"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});
		var response = await client.SendAsync(request);
		var auditOperation = await GetLatestAuditOperationAsync(new(appUserId));
		using var apiRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/employees/{appUserId}/rates");
		apiRequest.Headers.Authorization = new("Bearer", issued.Token);
		var apiResponse = await client.SendAsync(apiRequest);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var updatedSecurityStamp = await GetSecurityStampAsync(appUserId);
		var (enabled, _) = await GetTwoFactorStateAsync(appUserId);
		enabled.Should().BeFalse();
		updatedSecurityStamp.Should().NotBe(originalSecurityStamp);
		auditOperation.Should().Be("authentication.two-factor-disabled");
		apiResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Disabling_with_an_incorrect_password_does_not_clear_two_factor_state()
	{
		var appUserId = await SeedUserAsync("margaret.disable");
		var authCookie = await SignInAsync("margaret.disable");
		await SeedTwoFactorEnabledAsync(appUserId);

		var (antiforgeryCookie, token) = await GetFormAsync("/Account/ManageTwoFactor", authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/ManageTwoFactor?handler=Disable");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Disable.CurrentPassword"] = "wrong-password",
			["__RequestVerificationToken"] = token,
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var (enabled, _) = await GetTwoFactorStateAsync(appUserId);
		enabled.Should().BeTrue();
	}

	[Fact]
	public async Task A_requester_can_manage_their_two_factor_settings()
	{
		var appUserId = await SeedUserAsync("rita.twofactor", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.twofactor");

		var response = await GetFormAsync("/Account/ManageTwoFactor", authCookie);

		response.CookieHeader.Should().NotBeNullOrEmpty();
		var (enabled, _) = await GetTwoFactorStateAsync(appUserId);
		enabled.Should().BeFalse();
	}

	private async Task<string> SignInAsync(string userName)
	{
		var (antiforgeryCookie, token) = await GetFormAsync("/Account/Login");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});
		var response = await client.SendAsync(request);

		return ExtractCookiePair(
			FindSetCookie(response, "Identity.Application") ?? throw new InvalidOperationException("Sign-in did not set an auth cookie."));
	}

	private async Task<(string Secret, string AuthCookie, string AntiforgeryCookie, string Token)> GetEnrolmentFormAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/ManageTwoFactor");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		var refreshedAuthCookie = FindSetCookie(response, "Identity.Application") is { } reissued ? ExtractCookiePair(reissued) : authCookie;
		var antiforgeryCookie = ExtractCookiePair(
			FindSetCookie(response, "Antiforgery") ?? throw new InvalidOperationException("No antiforgery cookie in the enrolment page response."));
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } tokenMatch
			? tokenMatch.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in the enrolment page body.");
		var secret = AuthenticatorKeyPattern().Match(body) is { Success: true } keyMatch
			? keyMatch.Groups["key"].Value
			: throw new InvalidOperationException("No authenticator key in the enrolment page body.");

		return (secret, refreshedAuthCookie, antiforgeryCookie, token);
	}

	private async Task<HttpResponseMessage> PostConfirmAsync(string authCookie, string antiforgeryCookie, string token, string code)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/ManageTwoFactor?handler=Confirm");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Confirm.Code"] = code,
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

	private async Task<string> GetSecurityStampAsync(long appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT security_stamp FROM identity_user WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$appUserId", appUserId);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("Enter this key manually: <code>(?<key>[^<]+)</code>")]
	private static partial Regex AuthenticatorKeyPattern();

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

	private async Task<long> SeedUserAsync(string userName, EmployeeRole role = EmployeeRole.Worker)
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

		await using var insertIdentityUser = connection.CreateCommand();
		insertIdentityUser.CommandText = """
										 INSERT INTO identity_user
										 	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										 	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										 VALUES
										 	($appUserId, $userName, $normalizedUserName, $passwordHash, $securityStamp,
										 	 $concurrencyStamp, 0, 1, 1, 0);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", placeholderUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();

		await using var insertRole = connection.CreateCommand();
		insertRole.CommandText =
			"INSERT INTO identity_user_role (identity_user_id, identity_role_id) SELECT id, $roleId FROM identity_user WHERE app_user_id = $appUserId;";
		_ = insertRole.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)role);
		_ = await insertRole.ExecuteNonQueryAsync();

		return appUserId;
	}

	private async Task SeedTwoFactorEnabledAsync(long appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"UPDATE identity_user SET two_factor_enabled = 1, authenticator_key_protected = $key WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$key", new byte[] { 1, 2, 3 });
		_ = command.Parameters.AddWithValue("$appUserId", appUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<(bool Enabled, byte[]? KeyProtected)> GetTwoFactorStateAsync(long appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT two_factor_enabled, authenticator_key_protected FROM identity_user WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$appUserId", appUserId);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		var enabled = reader.GetBoolean(0);
		var keyProtected = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);

		return (enabled, keyProtected);
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

	private sealed class TestWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}
}
