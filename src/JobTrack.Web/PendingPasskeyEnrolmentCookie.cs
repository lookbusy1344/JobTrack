namespace JobTrack.Web;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Abstractions;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
///     Carries the pending passkey friendly name (and the account's WebAuthn user handle it was
///     validated for) across the two-POST enrolment ceremony (ADR 0071 §8.1) — a short-lived,
///     actor-bound, data-protected cookie, so no <c>identity_user_passkey</c> row or reserved name
///     exists until attestation succeeds. Written by the begin-add handler after the name passes every
///     check; consumed and deleted by the complete-add handler once the framework verifies the
///     credential. A cancelled, timed-out, or failed ceremony simply leaves the cookie to expire,
///     reserving nothing. The framework stores its own single-use attestation state separately; this
///     cookie holds only JobTrack's name, never WebAuthn ceremony state.
/// </summary>
internal static class PendingPasskeyEnrolmentCookie
{
	internal const string CookieName = "JobTrack.PendingPasskey";

	private const string ProtectorPurpose = "JobTrack.PendingPasskey";

	// Comfortably covers IdentityPasskeyOptions.AuthenticatorTimeout (5 minutes) plus the time the
	// user spends at the authenticator prompt, without outliving the ceremony by long.
	private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

	internal static void Publish(
		HttpContext httpContext, IDataProtectionProvider dataProtectionProvider, AppUserId actor, string name, string userHandle)
	{
		ArgumentNullException.ThrowIfNull(httpContext);
		ArgumentNullException.ThrowIfNull(dataProtectionProvider);
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(userHandle);

		var payload = JsonSerializer.Serialize(new Payload(name, userHandle));
		var protectedValue = CreateProtector(dataProtectionProvider, actor).Protect(payload, DefaultLifetime);
		httpContext.Response.Cookies.Append(
			CookieName,
			protectedValue,
			new() {
				HttpOnly = true,
				Secure = true,
				SameSite = SameSiteMode.Lax,
				IsEssential = true,
				MaxAge = DefaultLifetime,
			});
	}

	/// <summary>
	///     Decrypts and deletes the pending-name cookie in one step. Returns <see langword="false" />
	///     without side effects for a missing, expired, tampered, or wrong-actor cookie — the
	///     "setup expired" case, never an exception.
	/// </summary>
	internal static bool TryConsume(
		HttpContext httpContext, IDataProtectionProvider dataProtectionProvider, AppUserId actor, out string name, out string userHandle)
	{
		ArgumentNullException.ThrowIfNull(httpContext);
		ArgumentNullException.ThrowIfNull(dataProtectionProvider);

		name = string.Empty;
		userHandle = string.Empty;

		if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var cookieValue) || string.IsNullOrEmpty(cookieValue)) {
			return false;
		}

		try {
			var payload = JsonSerializer.Deserialize<Payload>(CreateProtector(dataProtectionProvider, actor).Unprotect(cookieValue));
			name = payload.Name;
			userHandle = payload.UserHandle;
			httpContext.Response.Cookies.Delete(CookieName);
			return true;
		}
		catch (CryptographicException) {
			return false;
		}
		catch (JsonException) {
			return false;
		}
	}

	private static ITimeLimitedDataProtector CreateProtector(IDataProtectionProvider dataProtectionProvider, AppUserId actor) =>
		dataProtectionProvider.CreateProtector(ProtectorPurpose, actor.Value.ToString(CultureInfo.InvariantCulture)).ToTimeLimitedDataProtector();

	private readonly record struct Payload(string Name, string UserHandle);
}
