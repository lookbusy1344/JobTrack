namespace JobTrack.Web.IntegrationTests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Program = Program;

internal sealed class TestWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		// Program.cs reads these values before Build(), so UseSetting is required here.
		_ = builder.UseEnvironment("Development");
		_ = builder.UseSetting("Database:Provider", "Sqlite");
		_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
	}
}
