namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Passkeys;
using Program = Program;

/// <summary>
///     ADR 0071 Stage 3: the relying-party policy is fixed (required discoverable credentials and user
///     verification, no attestation, unrestricted attachment) and an enabled feature fails startup
///     closed without a valid RP ID and exact origin allowlist. Validation logic is exercised directly;
///     the policy application and ceremony-handler DI are proven against the real host.
/// </summary>
public sealed class PasskeyAuthenticationConfigurationTests
{
	private const string SqliteConnectionString = "Data Source=:memory:";

	[Fact]
	public void A_disabled_feature_needs_no_relying_party_configuration()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = false,
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().NotThrow();
	}

	[Fact]
	public void An_enabled_feature_without_a_server_domain_is_rejected()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			Origins = ["https://jobs.example.com"],
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().Throw<InvalidOperationException>().WithMessage("*ServerDomain*");
	}

	[Fact]
	public void A_server_domain_carrying_a_scheme_or_port_is_rejected()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			ServerDomain = "https://jobs.example.com",
			Origins = ["https://jobs.example.com"],
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().Throw<InvalidOperationException>().WithMessage("*bare host*");
	}

	[Fact]
	public void An_enabled_feature_without_origins_is_rejected()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			ServerDomain = "jobs.example.com",
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().Throw<InvalidOperationException>().WithMessage("*Origins*");
	}

	[Fact]
	public void An_http_origin_is_rejected_when_https_is_required()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			ServerDomain = "jobs.example.com",
			Origins = ["http://jobs.example.com"],
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().Throw<InvalidOperationException>().WithMessage("*HTTPS*");
	}

	[Fact]
	public void An_http_localhost_origin_is_accepted_in_development()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			ServerDomain = "localhost",
			Origins = ["http://localhost:5001"],
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, false);

		act.Should().NotThrow();
	}

	[Fact]
	public void A_non_canonical_origin_with_a_trailing_slash_is_rejected()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			ServerDomain = "jobs.example.com",
			Origins = ["https://jobs.example.com/"],
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().Throw<InvalidOperationException>().WithMessage("*canonical*");
	}

	[Fact]
	public void An_origin_whose_host_does_not_match_the_relying_party_is_rejected()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			ServerDomain = "jobs.example.com",
			Origins = ["https://evil.example.org"],
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().Throw<InvalidOperationException>().WithMessage("*RP ID*");
	}

	[Fact]
	public void A_subdomain_origin_of_the_relying_party_is_accepted()
	{
		var options = new PasskeyFeatureOptions {
			Enabled = true,
			ServerDomain = "example.com",
			Origins = ["https://jobs.example.com"],
		};

		var act = () => PasskeyAuthenticationSetup.Validate(options, true);

		act.Should().NotThrow();
	}

	[Fact]
	public void The_relying_party_policy_fixes_discoverability_verification_attestation_and_attachment()
	{
		using var factory = new PasskeyConfiguredFactory(true, "jobs.example.com", ["https://jobs.example.com"]);

		var options = factory.Services.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value;

		options.ServerDomain.Should().Be("jobs.example.com");
		options.ResidentKeyRequirement.Should().Be("required");
		options.UserVerificationRequirement.Should().Be("required");
		options.AttestationConveyancePreference.Should().Be("none");
		options.AuthenticatorAttachment.Should().BeNull();
	}

	[Fact]
	public async Task ValidateOrigin_admits_a_configured_origin_and_rejects_others()
	{
		using var factory = new PasskeyConfiguredFactory(true, "jobs.example.com", ["https://jobs.example.com"]);
		var options = factory.Services.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value;
		var validateOrigin = options.ValidateOrigin;
		validateOrigin.Should().NotBeNull();

		var allowed = await validateOrigin!(Context("https://jobs.example.com"));
		var rejected = await validateOrigin(Context("https://evil.example.org"));
		var crossOrigin = await validateOrigin(Context("https://jobs.example.com", true));

		allowed.Should().BeTrue();
		rejected.Should().BeFalse();
		crossOrigin.Should().BeFalse();
	}

	private static PasskeyOriginValidationContext Context(string origin, bool crossOrigin = false) =>
		new() {
			HttpContext = new DefaultHttpContext(),
			Origin = origin,
			CrossOrigin = crossOrigin,
		};

	[Fact]
	public void The_passkey_ceremony_handler_is_registered_for_the_sign_in_manager()
	{
		using var factory = new PasskeyConfiguredFactory(true, "jobs.example.com", ["https://jobs.example.com"]);
		using var scope = factory.Services.CreateScope();

		var handler = scope.ServiceProvider.GetService<IPasskeyHandler<JobTrackIdentityUser>>();

		handler.Should().NotBeNull();
	}

	[Fact]
	public void Startup_fails_closed_when_the_feature_is_enabled_without_a_relying_party_id()
	{
		using var factory = new PasskeyConfiguredFactory(true, null, ["https://jobs.example.com"]);

		var act = () => factory.Services.GetService(typeof(IWebHostEnvironment));

		act.Should().Throw<InvalidOperationException>().WithMessage("*ServerDomain*");
	}

	private sealed class PasskeyConfiguredFactory(bool enabled, string? serverDomain, string[] origins) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", SqliteConnectionString);
			_ = builder.UseSetting($"{PasskeyFeatureOptions.SectionName}:Enabled", enabled ? "true" : "false");
			_ = builder.UseSetting($"{PasskeyFeatureOptions.SectionName}:ServerDomain", serverDomain ?? string.Empty);

			for (var index = 0; index < origins.Length; ++index) {
				_ = builder.UseSetting($"{PasskeyFeatureOptions.SectionName}:Origins:{index}", origins[index]);
			}
		}
	}
}
