namespace JobTrack.Web;

using System.Buffers.Text;
using System.Text.Json;
using Abstractions;

/// <summary>
///     A bounded reader for the credential ID inside a WebAuthn assertion response (ADR 0071 §7.2).
///     Used only after <c>SignInManager.PasskeySignInAsync</c> has already returned success, to resolve
///     the authenticated user through <c>UserManager.FindByPasskeyIdAsync</c> for audit and
///     forced-password-change routing. A pre-verification credential ID is never used to choose an
///     error message, actor, lockout target, or limiter partition — only this post-success lookup reads
///     it. Oversized JSON is rejected before parsing.
/// </summary>
internal static class PasskeyAssertionCredential
{
	// A WebAuthn assertion response is a small fixed-shape JSON object; a realistic one is a few
	// hundred bytes. This cap bounds the parse without constraining any legitimate authenticator.
	public static bool TryReadCredentialId(string credentialJson, out byte[] credentialId)
	{
		credentialId = [];
		if (string.IsNullOrEmpty(credentialJson) || credentialJson.Length > PasskeyPolicy.MaximumCredentialJsonByteLength) {
			return false;
		}

		try {
			using var document = JsonDocument.Parse(credentialJson);
			if (!document.RootElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) {
				return false;
			}

			var encoded = idElement.GetString();
			if (string.IsNullOrEmpty(encoded)) {
				return false;
			}

			credentialId = Base64Url.DecodeFromChars(encoded);
			return credentialId.Length > 0;
		}
		catch (JsonException) {
			return false;
		}
		catch (FormatException) {
			return false;
		}
	}
}
