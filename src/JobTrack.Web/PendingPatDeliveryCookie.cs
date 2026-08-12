namespace JobTrack.Web;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Abstractions;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
///     Replaces the in-process <c>PendingPatDeliveryStore</c> (ADR 0066 Stage 4,
///     docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.5) with a short-lived,
///     actor-bound, data-protected cookie carrying a freshly issued personal access token's plaintext
///     from the issuing POST to the redirected GET -- coherent across web hosts because the payload
///     travels with the request instead of living in one process's memory.
///     <see cref="PersonalAccessTokensModel.OnPostIssueAsync" /> calls <see cref="CanDeliver" />
///     *before* <c>ITokenCommands.IssueAsync</c> runs, preserving the old store's reserve-before-command
///     guarantee that a token is never minted only to find it cannot be delivered -- the check is a
///     pure size bound here (there is no cross-request capacity to exhaust once state is client-side),
///     but it still refuses before the command exactly as the old capacity-exhausted case did. On
///     success the POST calls <see cref="Publish" />, which writes the cookie only after the database
///     commit, then redirects with no plaintext in the URL, model state, or logs. The GET calls
///     <see cref="TryConsume" />, which decrypts and deletes the cookie in one step so a normal browser
///     never displays it twice.
///     <b>Documented residual</b> (plan §2.5): unlike the old store's true single-consumption
///     dictionary entry, deletion here is a client instruction (a Set-Cookie response header), not a
///     server-enforced one-time use -- a client that captures and replays the raw cookie value within
///     <see cref="DefaultLifetime" /> can decrypt it again. That replay can only ever reveal a token
///     already delivered once to that same authenticated actor (the protector purpose is bound to
///     Identity's own row id): it grants no new capability an already-authenticated actor's own
///     browser session did not already have, and it self-expires quickly. See
///     docs/threat-model/web-authentication-threat-model.md row 13.
/// </summary>
internal static class PendingPatDeliveryCookie
{
	internal const string CookieName = "JobTrack.PendingPat";

	// PersonalAccessTokenSecretGenerator always produces "jtpat_" plus 43 base64url characters --
	// fixed length regardless of label, so this pre-issuance size check can use a same-length
	// placeholder without depending on JobTrack.Application's internal generator.
	private const int PlaintextTokenLength = 49;

	// Label is bounded to 200 code points at the model-binding layer
	// (PersonalAccessTokensModel.IssueTokenInput.Label). System.Text.Json always \uXXXX-escapes a
	// character outside the Basic Multilingual Plane (a surrogate pair) regardless of encoder
	// settings -- worst case is therefore 12 bytes per code point (200 astral characters, 2400
	// bytes), not raw UTF-8's 4 -- plus the fixed-length token and JSON/property-name overhead,
	// while still being a real, enforced bound rather than an unbounded payload.
	internal const int MaxPayloadBytes = 2600;

	private const string ProtectorPurpose = "JobTrack.PendingPat";
	private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

	// The default encoder escapes every non-ASCII code point as \uXXXX (up to 12 bytes per
	// surrogate pair) rather than writing its raw UTF-8 bytes -- this payload is immediately
	// encrypted and never sent to a browser as literal JSON/HTML, so there is no injection surface
	// to defend against by escaping, and the relaxed encoder keeps a non-ASCII label's actual
	// footprint close to CanDeliver's byte-count estimate instead of triple-counting it.
	private static readonly JsonSerializerOptions SerializerOptions = new() {
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>
	///     True when a token issued under <paramref name="label" /> would fit the cookie's payload
	///     bound. Call before the issuing command runs -- see the type doc's reserve-before-command
	///     note.
	/// </summary>
	internal static bool CanDeliver(string label)
	{
		ArgumentNullException.ThrowIfNull(label);

		var placeholder = JsonSerializer.Serialize(new Payload(label, new('x', PlaintextTokenLength)), SerializerOptions);
		return Encoding.UTF8.GetByteCount(placeholder) <= MaxPayloadBytes;
	}

	/// <summary>Writes the protected delivery cookie. Call only after the issuing command has committed.</summary>
	internal static void Publish(
		HttpContext httpContext, IDataProtectionProvider dataProtectionProvider, AppUserId actor, string label, string plaintext,
		TimeSpan? lifetime = null)
	{
		ArgumentNullException.ThrowIfNull(httpContext);
		ArgumentNullException.ThrowIfNull(dataProtectionProvider);
		ArgumentNullException.ThrowIfNull(label);
		ArgumentNullException.ThrowIfNull(plaintext);

		var effectiveLifetime = lifetime ?? DefaultLifetime;
		var payload = JsonSerializer.Serialize(new Payload(label, plaintext), SerializerOptions);
		var protectedValue = CreateProtector(dataProtectionProvider, actor).Protect(payload, effectiveLifetime);
		httpContext.Response.Cookies.Append(
			CookieName,
			protectedValue,
			new() {
				HttpOnly = true,
				Secure = true,
				SameSite = SameSiteMode.Lax,
				IsEssential = true,
				MaxAge = effectiveLifetime,
			});
	}

	/// <summary>
	///     Decrypts and deletes the delivery cookie in one step. Returns <see langword="false" />
	///     without side effects for a missing, expired, tampered, or wrong-actor cookie -- exactly what
	///     "nothing pending" already meant, never a thrown exception or a broken page. The cookie is
	///     deleted only on a genuine successful decrypt: a wrong-actor or tampered attempt leaves it
	///     intact (matching the old store's "a wrong-actor guess leaves the real owner's slot intact"),
	///     rather than a bystander request destroying it before the real owner ever sees it.
	/// </summary>
	internal static bool TryConsume(HttpContext httpContext, IDataProtectionProvider dataProtectionProvider, AppUserId actor, out string label,
									out string plaintext)
	{
		ArgumentNullException.ThrowIfNull(httpContext);
		ArgumentNullException.ThrowIfNull(dataProtectionProvider);

		label = string.Empty;
		plaintext = string.Empty;

		if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var cookieValue) || string.IsNullOrEmpty(cookieValue)) {
			return false;
		}

		try {
			var payload = JsonSerializer.Deserialize<Payload>(CreateProtector(dataProtectionProvider, actor).Unprotect(cookieValue));
			label = payload.Label;
			plaintext = payload.Plaintext;
			httpContext.Response.Cookies.Delete(CookieName);
			return true;
		}
		catch (CryptographicException) {
			// Expired, tampered, or minted for a different principal -- same as no cookie at all.
			return false;
		}
		catch (JsonException) {
			return false;
		}
	}

	private static ITimeLimitedDataProtector CreateProtector(IDataProtectionProvider dataProtectionProvider, AppUserId actor) =>
		dataProtectionProvider.CreateProtector(ProtectorPurpose, actor.Value.ToString(CultureInfo.InvariantCulture)).ToTimeLimitedDataProtector();

	private readonly record struct Payload(string Label, string Plaintext);
}
