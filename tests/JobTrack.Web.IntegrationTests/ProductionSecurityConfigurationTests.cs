namespace JobTrack.Web.IntegrationTests;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using TestSupport;
using Program = Program;

/// <summary>
///     Plan §8.2 / fix-plan §2.4: outside Development, missing forwarded-header trust configuration
///     or a missing data-protection key path must fail startup closed rather than silently trusting an
///     unconfigured reverse-proxy boundary or falling back to an ephemeral key ring.
/// </summary>
public sealed class ProductionSecurityConfigurationTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string CertificatePassword = "data-protection-test-certificate";

	private readonly SqliteDatabaseFixture database = new();
	private string? certificatePasswordPath;
	private string? certificatePath;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);
		(certificatePath, certificatePasswordPath) = WriteDataProtectionCertificate();
	}

	public async Task DisposeAsync()
	{
		DeleteIfPresent(certificatePath);
		DeleteIfPresent(certificatePasswordPath);
		await database.DisposeAsync();
	}

	public void Dispose()
	{
	}

	[Fact]
	public void Startup_fails_closed_outside_development_without_forwarded_header_configuration()
	{
		using var factory = new UnconfiguredProductionWebApplicationFactory(database.ConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*ForwardedHeaders*trusted reverse proxy outside Development*");
	}

	[Fact]
	public void Startup_fails_closed_outside_development_without_a_data_protection_key_path()
	{
		using var factory = new UnconfiguredDataProtectionWebApplicationFactory(database.ConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:KeyPath*outside Development*");
	}

	[Fact]
	public void Startup_fails_closed_outside_development_without_data_protection_key_encryption()
	{
		using var factory = new UnconfiguredDataProtectionCertificateWebApplicationFactory(database.ConnectionString);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:CertificatePath*outside Development*");
	}

	/// <summary>
	///     Security-audit finding 6: <c>AllowedHosts</c> defaults to <c>"*"</c>, which disables host
	///     filtering entirely and leaves the deployment open to Host-header abuse. Outside Development it
	///     must name this deployment's own hosts, so the wildcard is rejected at startup rather than
	///     silently honoured.
	/// </summary>
	[Fact]
	public void Startup_fails_closed_outside_development_with_wildcard_allowed_hosts()
	{
		using var factory = new WildcardAllowedHostsWebApplicationFactory(
			database.ConnectionString, certificatePath!, certificatePasswordPath!);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedHosts*outside Development*");
	}

	[Fact]
	public void Startup_succeeds_outside_development_when_allowed_hosts_names_a_real_host()
	{
		using var factory = new ConfiguredProductionWebApplicationFactory(
			database.ConnectionString, certificatePath!, certificatePasswordPath!);

		var act = () => factory.Services.GetService(typeof(IHostEnvironment));

		act.Should().NotThrow();
	}



	private static (string CertificatePath, string PasswordPath) WriteDataProtectionCertificate()
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest("CN=JobTrack test data protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
		var certificatePath = Path.Combine(Path.GetTempPath(), $"jobtrack-dp-{Guid.NewGuid():N}.pfx");
		var passwordPath = Path.Combine(Path.GetTempPath(), $"jobtrack-dp-{Guid.NewGuid():N}.password");
		File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, CertificatePassword));
		File.WriteAllText(passwordPath, CertificatePassword);
		return (certificatePath, passwordPath);
	}

	private static void DeleteIfPresent(string? path)
	{
		if (path is not null && File.Exists(path)) {
			File.Delete(path);
		}
	}

	private sealed class UnconfiguredProductionWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}

	// Configures a trusted forwarded-header proxy (so that earlier fail-closed check passes) but
	// leaves DataProtection:KeyPath unset, isolating the data-protection half of the same
	// fail-closed startup sequence from the forwarded-headers half proven above.
	private sealed class UnconfiguredDataProtectionWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("ForwardedHeaders:KnownProxies:0", "127.0.0.1");
		}
	}

	private sealed class UnconfiguredDataProtectionCertificateWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("ForwardedHeaders:KnownProxies:0", "127.0.0.1");
			_ = builder.UseSetting("DataProtection:KeyPath", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		}
	}

	// Clears the two checks above, leaving only appsettings.json's AllowedHosts to be evaluated.
	private sealed class WildcardAllowedHostsWebApplicationFactory(
		string identityConnectionString,
		string certificatePath,
		string certificatePasswordPath) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("ForwardedHeaders:KnownProxies:0", "127.0.0.1");
			_ = builder.UseSetting("DataProtection:KeyPath", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
			_ = builder.UseSetting("DataProtection:CertificatePath", certificatePath);
			_ = builder.UseSetting("DataProtection:CertificatePasswordPath", certificatePasswordPath);
			_ = builder.UseSetting("AllowedHosts", "jobtrack.example;*");
		}
	}

	// The positive control for the three fail-closed checks above: a fully configured Production host
	// starts, so those tests are proven to fail on the setting under test rather than on the fact that
	// the environment is Production at all.
	private sealed class ConfiguredProductionWebApplicationFactory(
		string identityConnectionString,
		string certificatePath,
		string certificatePasswordPath) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Production");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("ForwardedHeaders:KnownProxies:0", "127.0.0.1");
			_ = builder.UseSetting("DataProtection:KeyPath", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
			_ = builder.UseSetting("DataProtection:CertificatePath", certificatePath);
			_ = builder.UseSetting("DataProtection:CertificatePasswordPath", certificatePasswordPath);
			_ = builder.UseSetting("AllowedHosts", "jobtrack.example");
		}
	}
}
