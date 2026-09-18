namespace JobTrack.Web.IntegrationTests;

using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Program = Program;

internal sealed class TestWebApplicationFactory(
	string identityConnectionString,
	bool enablePasskeys = false,
	ILoginAttemptRateLimiter? loginAttemptRateLimiter = null,
	IPasswordHasher<JobTrackIdentityUser>? passwordHasher = null) : WebApplicationFactory<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		// Program.cs reads these values before Build(), so UseSetting is required here.
		_ = builder.UseEnvironment("Development");
		_ = builder.UseSetting("Database:Provider", "Sqlite");
		_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		_ = builder.UseSetting("Authentication:Passkeys:Enabled", enablePasskeys ? "true" : "false");
		if (enablePasskeys) {
			// The TestServer serves the app at http://localhost, so the RP ID is "localhost" and the one
			// allowed origin is http://localhost (HTTP is permitted outside Production).
			_ = builder.UseSetting("Authentication:Passkeys:ServerDomain", "localhost");
			_ = builder.UseSetting("Authentication:Passkeys:Origins:0", "http://localhost");
		}

		if (loginAttemptRateLimiter is not null) {
			_ = builder.ConfigureTestServices(services => {
				services.RemoveAll<ILoginAttemptRateLimiter>();
				_ = services.AddSingleton(loginAttemptRateLimiter);
			});
		}

		if (passwordHasher is not null) {
			_ = builder.ConfigureTestServices(services => {
				services.RemoveAll<IPasswordHasher<JobTrackIdentityUser>>();
				_ = services.AddSingleton(passwordHasher);
			});
		}
	}
}
