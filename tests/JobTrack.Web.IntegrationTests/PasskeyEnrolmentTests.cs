namespace JobTrack.Web.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Abstractions;
using AwesomeAssertions;
using Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestSupport;
using Program = Program;

/// <summary>
///     Passkey enrolment and management on <c>/Account/Security</c> (ADR 0071 §8.1): the two-POST add
///     ceremony (name collected and validated before the chooser opens; nothing reserved until
///     attestation succeeds) and the rename/remove PRG handlers (rename is audited metadata, remove
///     rotates the security stamp). The virtual-authenticator add happy path is Stage 7; these cover
///     the server-side validation, ceremony-state, management, and failure branches over real HTTP
///     against SQLite.
/// </summary>
public sealed partial class PasskeyEnrolmentTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string PendingPasskeyCookieName = "JobTrack.PendingPasskey";

	private readonly ConcurrentBag<string> capturedLogEntries = [];
	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private LoggingPasskeyFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		factory = new(database.ConnectionString, capturedLogEntries);
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
	public async Task Begin_add_with_a_valid_name_returns_the_ceremony_without_storing_a_passkey()
	{
		var appUserId = await SeedUserAsync("ada.enrol");
		var authCookie = await SignInAsync("ada.enrol");

		var response = await BeginAddAsync(authCookie, "Work MacBook");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("data-passkey-ceremony");
		body.Should().Contain("data-creation-options");
		(await CountPasskeysAsync(appUserId)).Should().Be(0);
		(await GetPasskeyUserHandleAsync(appUserId)).Should().NotBeNull("the account handle is ensured before options are generated");
	}

	[Fact]
	public async Task Begin_add_with_a_blank_name_is_rejected_without_a_ceremony()
	{
		var appUserId = await SeedUserAsync("grace.enrol");
		var authCookie = await SignInAsync("grace.enrol");

		var response = await BeginAddAsync(authCookie, "   ");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("data-passkey-ceremony");
		body.Should().Contain("Enter a name for the passkey");
		(await CountPasskeysAsync(appUserId)).Should().Be(0);
	}

	[Fact]
	public async Task Begin_add_with_a_duplicate_name_is_rejected()
	{
		var appUserId = await SeedUserAsync("kat.enrol");
		var authCookie = await SignInAsync("kat.enrol");
		await SeedPasskeyAsync(appUserId, "Work MacBook", [1, 2, 3]);

		var response = await BeginAddAsync(authCookie, "work macbook");
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain("data-passkey-ceremony");
		body.Should().Contain("already have a passkey with that name");
	}

	[Fact]
	public async Task Begin_add_at_the_maximum_count_is_rejected()
	{
		var appUserId = await SeedUserAsync("max.enrol");
		var authCookie = await SignInAsync("max.enrol");
		for (var index = 0; index < 10; ++index) {
			await SeedPasskeyAsync(appUserId, $"Key {index}", [(byte)index, 9, 9]);
		}

		var response = await BeginAddAsync(authCookie, "Eleventh");
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain("data-passkey-ceremony");
		body.Should().Contain("maximum of 10 passkeys");
	}

	[Fact]
	public async Task Complete_add_without_a_pending_ceremony_reports_expiry()
	{
		_ = await SeedUserAsync("noceremony.enrol");
		var authCookie = await SignInAsync("noceremony.enrol");
		var (antiforgeryCookie, token) = await GetAntiforgeryAsync("/Account/Security", authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Security?handler=CompleteAddPasskey");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(new
		{
			id = "AAAA",
		});
		var response = await client.SendAsync(request);
		var payload = await response.Content.ReadAsStringAsync();

		payload.Should().Contain("Passkey setup expired");
		payload.Should().NotContain("redirect");
	}

	[Fact]
	public async Task Complete_add_with_an_invalid_credential_fails_generically_and_stores_nothing()
	{
		var appUserId = await SeedUserAsync("bad.enrol");
		var authCookie = await SignInAsync("bad.enrol");
		var (cookies, token) = await BeginAddForCompletionAsync(authCookie, "Blue YubiKey");

		var response = await CompleteAddWithInvalidCredentialAsync(cookies, token);
		var payload = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK, "the handler runs and returns a JSON error, not an antiforgery 400");
		payload.Should().NotContain("redirect");
		(await CountPasskeysAsync(appUserId)).Should().Be(0, "a failed attestation reserves no credential or name");
	}

	[Fact]
	public async Task Complete_add_without_a_pending_ceremony_logs_the_discarded_reason()
	{
		_ = await SeedUserAsync("logmissing.enrol");
		var authCookie = await SignInAsync("logmissing.enrol");
		var (antiforgeryCookie, token) = await GetAntiforgeryAsync("/Account/Security", authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Security?handler=CompleteAddPasskey");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(new
		{
			id = "AAAA",
		});
		_ = await client.SendAsync(request);

		capturedLogEntries.Should().Contain(
			entry => entry.Contains("passkey_enrolment_rejected") && entry.Contains("stage=ceremony-state-missing"),
			"the opaque browser response must still leave the operator the real reason");
	}

	[Fact]
	public async Task Complete_add_with_an_invalid_credential_logs_the_failure_reason()
	{
		_ = await SeedUserAsync("logbad.enrol");
		var authCookie = await SignInAsync("logbad.enrol");
		var (cookies, token) = await BeginAddForCompletionAsync(authCookie, "Blue YubiKey");

		var response = await CompleteAddWithInvalidCredentialAsync(cookies, token);

		response.StatusCode.Should().Be(HttpStatusCode.OK, "the antiforgery-valid POST reaches the handler");
		capturedLogEntries.Should().Contain(
			entry => entry.Contains("passkey_enrolment_failed") || entry.Contains("passkey_attestation_unverified"),
			"a rejected attestation is logged, not collapsed into a silent generic response");
	}

	[Fact]
	public async Task Enrolment_is_hidden_and_refused_when_the_feature_is_disabled()
	{
		using var disabledFactory = new TestWebApplicationFactory(database.ConnectionString);
		using var disabledClient = disabledFactory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});

		_ = await SeedUserAsync("disabled.enrol");
		var authCookie = await SignInWithAsync(disabledClient, "disabled.enrol");

		using var getRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/Security");
		getRequest.Headers.Add("Cookie", authCookie);
		var getResponse = await disabledClient.SendAsync(getRequest);
		var body = await getResponse.Content.ReadAsStringAsync();

		var (antiforgeryCookie, token) = await GetAntiforgeryWithAsync(disabledClient, "/Account/Security", authCookie);
		using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Security?handler=BeginAddPasskey");
		postRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		postRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Add.Name"] = "Whatever",
			["__RequestVerificationToken"] = token,
		});
		var postResponse = await disabledClient.SendAsync(postRequest);

		body.Should().NotContain("data-passkey-add-form");
		postResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Rename_changes_an_owned_passkey_name()
	{
		var appUserId = await SeedUserAsync("rename.owner");
		var authCookie = await SignInAsync("rename.owner");
		await SeedPasskeyAsync(appUserId, "Old name", [1, 2, 3]);

		var response = await PostManagementAsync(authCookie, "RenamePasskey", new() {
			["credentialRef"] = CredentialRef("AQID"),
			["newName"] = "New name",
		});

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		(await GetPasskeyNameAsync("AQID")).Should().Be("New name");
	}

	[Fact]
	public async Task Rename_to_a_name_already_used_leaves_the_passkey_unchanged()
	{
		var appUserId = await SeedUserAsync("rename.dup");
		var authCookie = await SignInAsync("rename.dup");
		await SeedPasskeyAsync(appUserId, "First", [1, 2, 3]);
		await SeedPasskeyAsync(appUserId, "Second", [4, 5, 6]);

		var response = await PostManagementAsync(authCookie, "RenamePasskey", new() {
			["credentialRef"] = CredentialRef("BAUG"),
			["newName"] = "first",
		});

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		(await GetPasskeyNameAsync("BAUG")).Should().Be("Second", "a duplicate rename is refused");
	}

	[Fact]
	public async Task Remove_deletes_an_owned_passkey_and_rotates_the_security_stamp()
	{
		var appUserId = await SeedUserAsync("remove.owner");
		var authCookie = await SignInAsync("remove.owner");
		await SeedPasskeyAsync(appUserId, "Doomed", [4, 5, 6]);
		var originalStamp = await GetSecurityStampAsync(appUserId);

		var response = await PostManagementAsync(authCookie, "RemovePasskey", new() {
			["credentialRef"] = CredentialRef("BAUG"),
		});

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		(await CountPasskeysAsync(appUserId)).Should().Be(0);
		(await GetSecurityStampAsync(appUserId)).Should().NotBe(originalStamp);
	}

	// The page never exposes the raw credential ID; rename/remove address a row through a
	// Data-Protection token of it (ADR 0071 §8.1). Mint the same token here from the app's own provider,
	// wrapping the base64url credential ID the page would protect.
	private string CredentialRef(string opaqueCredentialId) =>
		factory.Services.GetRequiredService<IDataProtectionProvider>()
			   .CreateProtector("JobTrack.Web.PasskeyCredentialRef.v1")
			   .Protect(opaqueCredentialId);

	private async Task<HttpResponseMessage> PostManagementAsync(string authCookie, string handler, Dictionary<string, string> fields)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryAsync("/Account/Security", authCookie);
		fields["__RequestVerificationToken"] = token;

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Account/Security?handler={handler}");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(fields);
		return await client.SendAsync(request);
	}

	private async Task<string?> GetPasskeyNameAsync(string opaqueCredentialId)
	{
		var credentialId = Convert.FromBase64String(opaqueCredentialId.Replace('-', '+').Replace('_', '/'));
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM identity_user_passkey WHERE credential_id = $credentialId;";
		_ = command.Parameters.AddWithValue("$credentialId", credentialId);
		var value = await command.ExecuteScalarAsync();
		return value is DBNull or null ? null : (string)value;
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

	private async Task<HttpResponseMessage> BeginAddAsync(string authCookie, string name)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryAsync("/Account/Security", authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Security?handler=BeginAddPasskey");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Add.Name"] = name,
			["__RequestVerificationToken"] = token,
		});
		return await client.SendAsync(request);
	}

	// Drives the begin-add POST and returns everything the complete POST needs: the auth session, the
	// pending-enrolment cookie the begin response sets, and — crucially — the same antiforgery cookie the
	// token was minted against, which BeginAddAsync otherwise discards (leaving the complete POST to fail
	// antiforgery with a 400 before ever reaching the handler).
	private async Task<(string CookieHeader, string Token)> BeginAddForCompletionAsync(string authCookie, string name)
	{
		var beginResponse = await BeginAddAsync(authCookie, name);
		var pendingCookie = WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(beginResponse, PendingPasskeyCookieName)
			?? throw new InvalidOperationException("Begin-add did not set the pending-enrolment cookie."));

		// The begin response rotates the antiforgery cookie, so a fresh pair is minted against the session
		// that now carries the pending cookie -- reusing the pre-begin token would fail validation.
		var (antiforgeryCookie, token) = await GetAntiforgeryAsync("/Account/Security", $"{authCookie}; {pendingCookie}");
		return ($"{authCookie}; {pendingCookie}; {antiforgeryCookie}", token);
	}

	private async Task<HttpResponseMessage> CompleteAddWithInvalidCredentialAsync(string cookieHeader, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Security?handler=CompleteAddPasskey");
		request.Headers.Add("Cookie", cookieHeader);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(new
		{
			id = "AAAA",
			rawId = "AAAA",
			type = "public-key",
			clientExtensionResults = new { },
			response = new
			{
				clientDataJSON = "AAAA",
				attestationObject = "AAAA",
			},
		});
		return await client.SendAsync(request);
	}

	private Task<string> SignInAsync(string userName) => SignInWithAsync(client, userName);

	private static async Task<string> SignInWithAsync(HttpClient httpClient, string userName)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryWithAsync(httpClient, "/Account/Login", null);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});
		var response = await httpClient.SendAsync(request);

		return WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(response, "Identity.Application") ?? throw new InvalidOperationException("Sign-in did not set an auth cookie."));
	}

	private Task<(string CookieHeader, string Token)> GetAntiforgeryAsync(string path, string? extraCookie) =>
		GetAntiforgeryWithAsync(client, path, extraCookie);

	private static async Task<(string CookieHeader, string Token)> GetAntiforgeryWithAsync(HttpClient httpClient, string path, string? extraCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (extraCookie is not null) {
			request.Headers.Add("Cookie", extraCookie);
		}

		var response = await httpClient.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(body).Groups["token"].Value;

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<long> CountPasskeysAsync(long appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT COUNT(*) FROM identity_user_passkey
							  WHERE identity_user_id = (SELECT id FROM identity_user WHERE app_user_id = $appUserId);
							  """;
		_ = command.Parameters.AddWithValue("$appUserId", appUserId);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private async Task<string?> GetPasskeyUserHandleAsync(long appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT passkey_user_handle FROM identity_user WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$appUserId", appUserId);
		var value = await command.ExecuteScalarAsync();
		return value is DBNull or null ? null : (string)value;
	}

	private async Task SeedPasskeyAsync(long appUserId, string name, byte[] credentialId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO identity_user_passkey
							  	(credential_id, identity_user_id, name, normalized_name, public_key, created_at, sign_count,
							  	 is_user_verified, is_backup_eligible, is_backed_up, aaguid, attestation_object, client_data_json, row_version)
							  SELECT $credentialId, id, $name, $normalizedName, $publicKey, 0, 0, 1, 0, 0, $aaguid, $attestation, $clientData, 1
							  FROM identity_user WHERE app_user_id = $appUserId;
							  """;
		_ = command.Parameters.AddWithValue("$credentialId", credentialId);
		_ = command.Parameters.AddWithValue("$name", name);
		_ = command.Parameters.AddWithValue("$normalizedName", name.ToUpperInvariant());
		_ = command.Parameters.AddWithValue("$publicKey", new byte[] {
			9,
		});
		_ = command.Parameters.AddWithValue("$aaguid", new byte[16]);
		_ = command.Parameters.AddWithValue("$attestation", new byte[] {
			8,
		});
		_ = command.Parameters.AddWithValue("$clientData", new byte[] {
			7,
		});
		_ = command.Parameters.AddWithValue("$appUserId", appUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<long> SeedUserAsync(string userName)
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
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)EmployeeRole.Worker);
		_ = await insertRole.ExecuteNonQueryAsync();

		return appUserId;
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	// The passkey-enabled factory of TestWebApplicationFactory with a log-capturing provider attached,
	// so the enrolment-failure logging (which the browser only ever sees as a generic message) can be
	// asserted directly.
	private sealed class LoggingPasskeyFactory(string identityConnectionString, ConcurrentBag<string> capturedLogEntries)
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
			_ = builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(capturedLogEntries)));
		}
	}

	private sealed class CapturingLoggerProvider(ConcurrentBag<string> capturedLogEntries) : ILoggerProvider
	{
		public ILogger CreateLogger(string categoryName) => new CapturingLogger(capturedLogEntries);

		public void Dispose() { }

		private sealed class CapturingLogger(ConcurrentBag<string> capturedLogEntries) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(
				LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
				capturedLogEntries.Add(formatter(state, exception));
		}
	}
}
