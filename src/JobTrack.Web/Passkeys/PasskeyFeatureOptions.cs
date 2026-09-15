namespace JobTrack.Web.Passkeys;

/// <summary>
///     The <c>Authentication:Passkeys</c> configuration section (ADR 0071 §6/§10). The feature is
///     off unless <see cref="Enabled" /> is set; when on, an explicit RP ID (<see cref="ServerDomain" />)
///     and exact HTTPS origin allowlist (<see cref="Origins" />) are required and validated at startup.
/// </summary>
public sealed class PasskeyFeatureOptions
{
	public const string SectionName = "Authentication:Passkeys";

	/// <summary>Gates the feature. Disabling hides enrolment and sign-in but retains stored credentials.</summary>
	public bool Enabled { get; set; }

	/// <summary>The WebAuthn RP ID — a bare host name, never inferred from a Host header. A durable credential namespace.</summary>
	public string? ServerDomain { get; set; }

	/// <summary>The exact allowed WebAuthn origins in canonical <c>scheme://host[:port]</c> form. No wildcards or suffix matches.</summary>
	public IReadOnlyList<string> Origins { get; set; } = [];
}
