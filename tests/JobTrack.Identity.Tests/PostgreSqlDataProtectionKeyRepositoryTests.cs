namespace JobTrack.Identity.Tests;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TestSupport;

/// <summary>
///     ADR 0066 Stage 2 (docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.2):
///     <see cref="PostgreSqlJobTrackIdentityDbContext" />'s <c>data_protection_key</c> table as ASP.NET
///     Core Data Protection's EF Core key repository. Two independently configured
///     <see cref="IDataProtectionProvider" /> instances, each with its own <c>PersistKeysToDbContext</c>
///     wiring against the same PostgreSQL database and the same protecting certificate, stand in for
///     two web hosts sharing one key ring -- the cross-instance proof
///     <see cref="Web.IntegrationTests.TwoHostPostgreSqlAcceptanceTests" /> exercises over real HTTP.
/// </summary>
public sealed class PostgreSqlDataProtectionKeyRepositoryTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string DataProtectionApplicationName = "JobTrack";
	private const string CertificateSubject = "CN=jobtrack-data-protection-test";
	private const string ProtectedPayload = "the quick brown fox";

	private readonly PostgreSqlDatabaseFixture database = new();

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();
	}

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_key_stored_through_one_provider_is_visible_through_a_fresh_DbContext()
	{
		using var certificate = CreateSelfSignedCertificate();
		var provider = CreateProvider(certificate);

		// Forces XmlKeyManager to generate and persist a key rather than only reading an existing
		// (empty) ring.
		var protector = provider.CreateProtector(nameof(A_key_stored_through_one_provider_is_visible_through_a_fresh_DbContext));
		_ = protector.Protect(ProtectedPayload);

		await using var context = CreateContext();
		var keys = await context.DataProtectionKeys.ToListAsync();

		keys.Should().ContainSingle();
		keys[0].Xml.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task The_persisted_key_XML_is_certificate_encrypted_not_plaintext()
	{
		using var certificate = CreateSelfSignedCertificate();
		var provider = CreateProvider(certificate);
		var protector = provider.CreateProtector(nameof(The_persisted_key_XML_is_certificate_encrypted_not_plaintext));
		_ = protector.Protect(ProtectedPayload);

		await using var context = CreateContext();
		var key = await context.DataProtectionKeys.SingleAsync();

		// CertificateXmlEncryptor wraps the plaintext <descriptor> in standard XML Encryption
		// (http://www.w3.org/2001/04/xmlenc#); the raw master-key material never appears in the
		// persisted row once a certificate is configured.
		key.Xml.Should().Contain("EncryptedData");
		key.Xml.Should().NotContain("<value>");
	}

	[Fact]
	public void A_payload_protected_through_one_provider_is_unprotected_through_an_independent_provider_sharing_the_certificate()
	{
		using var certificate = CreateSelfSignedCertificate();
		var providerA = CreateProvider(certificate);
		var providerB = CreateProvider(certificate);

		var protectorA =
			providerA.CreateProtector(
				nameof(A_payload_protected_through_one_provider_is_unprotected_through_an_independent_provider_sharing_the_certificate));
		var protected1 = protectorA.Protect(ProtectedPayload);

		var protectorB =
			providerB.CreateProtector(
				nameof(A_payload_protected_through_one_provider_is_unprotected_through_an_independent_provider_sharing_the_certificate));
		var unprotected = protectorB.Unprotect(protected1);

		unprotected.Should().Be(ProtectedPayload);
	}

	private IDataProtectionProvider CreateProvider(X509Certificate2 certificate)
	{
		var services = new ServiceCollection();
		_ = services.AddDbContext<PostgreSqlJobTrackIdentityDbContext>(options => options.UseNpgsql(database.ConnectionString));
		_ = services.AddDataProtection()
			.SetApplicationName(DataProtectionApplicationName)
			.PersistKeysToDbContext<PostgreSqlJobTrackIdentityDbContext>()
			.ProtectKeysWithCertificate(certificate);

		return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
	}

	private PostgreSqlJobTrackIdentityDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackIdentityDbContext>()
			.UseNpgsql(database.ConnectionString)
			.Options;

		return new(options);
	}

	private static X509Certificate2 CreateSelfSignedCertificate()
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest(CertificateSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
