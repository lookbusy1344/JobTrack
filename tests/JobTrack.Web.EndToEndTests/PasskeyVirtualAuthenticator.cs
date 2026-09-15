namespace JobTrack.Web.EndToEndTests;

using System.Text.Json;
using Microsoft.Playwright;

/// <summary>
///     Drives Chromium's CDP WebAuthn virtual authenticator (ADR 0071 Stage 7): a software authenticator
///     that satisfies <c>navigator.credentials.create</c>/<c>.get</c> without physical hardware or user
///     interaction, so the native ASP.NET Core ceremony is exercised end-to-end over real HTTPS. Chromium
///     only — the other Playwright engines expose no equivalent.
/// </summary>
internal sealed class PasskeyVirtualAuthenticator
{
	private readonly string authenticatorId;
	private readonly ICDPSession session;

	private PasskeyVirtualAuthenticator(ICDPSession session, string authenticatorId)
	{
		this.session = session;
		this.authenticatorId = authenticatorId;
	}

	/// <summary>
	///     Attaches a discoverable-credential, user-verifying internal authenticator to the page's browser
	///     context. <c>isUserVerified</c>/<c>automaticPresenceSimulation</c> make every ceremony succeed
	///     without a prompt, matching a platform authenticator whose biometric/PIN has already been given.
	/// </summary>
	public static async Task<PasskeyVirtualAuthenticator> AttachAsync(IBrowserContext context, IPage page)
	{
		var session = await context.NewCDPSessionAsync(page);
		_ = await session.SendAsync("WebAuthn.enable");
		var added = await session.SendAsync("WebAuthn.addVirtualAuthenticator", new() {
			["options"] = new Dictionary<string, object> {
				["protocol"] = "ctap2",
				["transport"] = "internal",
				["hasResidentKey"] = true,
				["hasUserVerification"] = true,
				["isUserVerified"] = true,
				["automaticPresenceSimulation"] = true,
			},
		});

		if (added is not JsonElement addedElement || !addedElement.TryGetProperty("authenticatorId", out var id)) {
			throw new InvalidOperationException("addVirtualAuthenticator returned no authenticatorId.");
		}

		var authenticatorId = id.GetString()
							  ?? throw new InvalidOperationException("addVirtualAuthenticator returned a null authenticatorId.");
		return new(session, authenticatorId);
	}

	/// <summary>How many discoverable credentials the attached authenticator currently holds.</summary>
	public async Task<int> CredentialCountAsync()
	{
		var result = await session.SendAsync("WebAuthn.getCredentials", new() {
			["authenticatorId"] = authenticatorId,
		});
		return result is JsonElement element && element.TryGetProperty("credentials", out var credentials)
			? credentials.GetArrayLength()
			: 0;
	}
}
