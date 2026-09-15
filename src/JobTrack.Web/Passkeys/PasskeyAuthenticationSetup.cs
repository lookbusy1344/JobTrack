namespace JobTrack.Web.Passkeys;

using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
///     Wires the ASP.NET Core passkey ceremony framework for <c>JobTrack.Web</c> (ADR 0071 §5, plan
///     §7.2). Configures <see cref="IdentityPasskeyOptions" /> with JobTrack's fixed relying-party
///     policy — required discoverable credentials and user verification, no attestation collection,
///     unrestricted authenticator attachment — plus the configured RP ID and exact origin allowlist.
///     Lives here, not in <c>JobTrack.Identity</c>, because these types belong to the ASP.NET Core
///     shared framework that project deliberately does not reference (ADR 0022).
/// </summary>
public static class PasskeyAuthenticationSetup
{
	private const string ResidentKeyRequired = "required";
	private const string UserVerificationRequired = "required";
	private const string AttestationNone = "none";

	/// <summary>
	///     Binds the passkey configuration, fails startup closed on invalid configuration, registers the
	///     framework ceremony handler, and applies the relying-party policy. <paramref name="requireHttpsOrigins" />
	///     is true outside Development so production cannot enrol against an HTTP origin.
	/// </summary>
	public static void Configure(IServiceCollection services, IConfiguration configuration, bool requireHttpsOrigins)
	{
		var options = Bind(configuration);
		Validate(options, requireHttpsOrigins);

		// Expose the bound feature options so pages can gate enrolment/sign-in UI on Enabled (ADR 0071 §10).
		_ = services.Configure<PasskeyFeatureOptions>(configuration.GetSection(PasskeyFeatureOptions.SectionName));

		// SignInManager.PasskeySignInAsync resolves the handler from request services at ceremony time;
		// AddIdentityCore does not register it, so wire the framework default explicitly.
		services.TryAddScoped<IPasskeyHandler<JobTrackIdentityUser>, PasskeyHandler<JobTrackIdentityUser>>();

		var allowedOrigins = new HashSet<string>(options.Origins, StringComparer.Ordinal);
		_ = services.Configure<IdentityPasskeyOptions>(passkey => {
			passkey.ServerDomain = options.ServerDomain;
			passkey.ResidentKeyRequirement = ResidentKeyRequired;
			passkey.UserVerificationRequirement = UserVerificationRequired;
			passkey.AttestationConveyancePreference = AttestationNone;
			passkey.AuthenticatorAttachment = null;
			passkey.ValidateOrigin = context =>
				ValueTask.FromResult(!context.CrossOrigin && context.Origin is not null && allowedOrigins.Contains(context.Origin));
		});
	}

	/// <summary>Binds the <c>Authentication:Passkeys</c> section to a <see cref="PasskeyFeatureOptions" />.</summary>
	public static PasskeyFeatureOptions Bind(IConfiguration configuration)
	{
		var options = new PasskeyFeatureOptions();
		configuration.GetSection(PasskeyFeatureOptions.SectionName).Bind(options);
		return options;
	}

	/// <summary>
	///     Throws <see cref="InvalidOperationException" /> when the feature is enabled without a valid RP
	///     ID and exact origin allowlist. A disabled feature validates trivially.
	/// </summary>
	public static void Validate(PasskeyFeatureOptions options, bool requireHttpsOrigins)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (!options.Enabled) {
			return;
		}

		var serverDomain = options.ServerDomain;
		if (string.IsNullOrWhiteSpace(serverDomain)) {
			throw new InvalidOperationException($"{PasskeyFeatureOptions.SectionName}:ServerDomain is required when passkeys are enabled.");
		}

		if (serverDomain.Contains('/', StringComparison.Ordinal) || serverDomain.Contains(':', StringComparison.Ordinal)) {
			throw new InvalidOperationException(
				$"{PasskeyFeatureOptions.SectionName}:ServerDomain must be a bare host name (RP ID), with no scheme, port, or path.");
		}

		if (options.Origins.Count == 0) {
			throw new InvalidOperationException(
				$"{PasskeyFeatureOptions.SectionName}:Origins must list at least one exact origin when passkeys are enabled.");
		}

		foreach (var origin in options.Origins) {
			ValidateOrigin(origin, serverDomain, requireHttpsOrigins);
		}
	}

	private static void ValidateOrigin(string origin, string serverDomain, bool requireHttpsOrigins)
	{
		if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) {
			throw new InvalidOperationException($"{PasskeyFeatureOptions.SectionName}:Origins entry '{origin}' is not a valid absolute origin.");
		}

		var canonical = uri.GetLeftPart(UriPartial.Authority);
		if (!string.Equals(origin, canonical, StringComparison.Ordinal)) {
			throw new InvalidOperationException(
				$"{PasskeyFeatureOptions.SectionName}:Origins entry '{origin}' must be a canonical scheme://host[:port] origin with no path or trailing slash (expected '{canonical}').");
		}

		var schemeAllowed = uri.Scheme == Uri.UriSchemeHttps || !requireHttpsOrigins && uri.Scheme == Uri.UriSchemeHttp;
		if (!schemeAllowed) {
			throw new InvalidOperationException(
				$"{PasskeyFeatureOptions.SectionName}:Origins entry '{origin}' must use HTTPS.");
		}

		var matchesRelyingParty = string.Equals(uri.Host, serverDomain, StringComparison.OrdinalIgnoreCase)
								  || uri.Host.EndsWith("." + serverDomain, StringComparison.OrdinalIgnoreCase);
		if (!matchesRelyingParty) {
			throw new InvalidOperationException(
				$"{PasskeyFeatureOptions.SectionName}:Origins entry '{origin}' host does not match the RP ID '{serverDomain}'.");
		}
	}
}
