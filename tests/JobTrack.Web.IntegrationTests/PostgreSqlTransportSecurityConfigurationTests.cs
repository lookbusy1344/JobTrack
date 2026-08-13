namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Program = Program;

/// <summary>
///     Security review remediation §2.9: outside Development, a remote PostgreSQL connection string
///     without an authenticated encrypted channel must fail startup closed -- same shape as
///     <see cref="ProductionSecurityConfigurationTests" />'s forwarded-headers/data-protection/
///     allowed-hosts checks. No live PostgreSQL server needed: the check runs, and throws, before
///     anything attempts to open a connection.
/// </summary>
public sealed class PostgreSqlTransportSecurityConfigurationTests
{
	private const string RemoteInsecureConnectionString = "Host=db.example.internal;Database=jobtrack;SSL Mode=Prefer";
	private const string LoopbackConnectionString = "Host=127.0.0.1;Port=5432;Database=jobtrack";

	[Fact]
	public void Startup_fails_closed_outside_development_with_a_remote_insecure_identity_connection_string()
	{
		using var factory = new PostgreSqlWebApplicationFactory(RemoteInsecureConnectionString, LoopbackConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*db.example.internal*");
	}

	[Fact]
	public void Startup_fails_closed_outside_development_with_a_remote_insecure_domain_connection_string()
	{
		using var factory = new PostgreSqlWebApplicationFactory(LoopbackConnectionString, RemoteInsecureConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*db.example.internal*");
	}

	/// <summary>
	///     The positive control: loopback connection strings for both keys pass the transport-security
	///     check regardless of environment, proving the two tests above fail on the remote+insecure
	///     connection string under test rather than on being PostgreSQL/Production at all. Building the
	///     host still throws (no forwarded-header/data-protection/allowed-hosts configuration is set,
	///     and no real PostgreSQL server is listening), but never with the transport-security message.
	/// </summary>
	[Fact]
	public void A_loopback_connection_string_never_fails_the_transport_security_check()
	{
		using var factory = new PostgreSqlWebApplicationFactory(LoopbackConnectionString, LoopbackConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<Exception>().Which.Message.Should().NotContain("SSL Mode");
	}

	private sealed class PostgreSqlWebApplicationFactory(string identityConnectionString, string domainConnectionString)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "PostgreSql");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackDomain", domainConnectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackHistoryDeletion", domainConnectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackCredentialAdministration", domainConnectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackPatManagement", domainConnectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackPatAuthentication", domainConnectionString);
		}
	}
}
