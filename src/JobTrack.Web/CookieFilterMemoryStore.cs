namespace JobTrack.Web;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
///     <see cref="FilterMemory" />'s storage seam (ADR 0066 Stage 3,
///     docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.3) -- narrow enough that a fake
///     in unit tests never needs cookies, HTTP, or data protection at all.
/// </summary>
internal interface IFilterMemoryStore
{
	string? GetString(string key);

	void SetString(string key, string value);

	void Clear();
}

/// <summary>
///     Replaces ASP.NET Core session (<c>AddDistributedMemoryCache</c>/<c>AddSession</c>, both
///     process-local) with one small, time-limited, data-protected, principal-bound cookie holding
///     every remembered filter as an ordered key/value list -- coherent across web hosts because the
///     state now travels with the request instead of living in one process's memory (ADR 0066 Stage
///     3). Unlike server-side session, a cookie cannot grow without limit: <see cref="MaxKeyCount" />
///     and <see cref="MaxPayloadBytes" /> are enforced by evicting the least-recently-set entry (list
///     order doubles as recency order -- <see cref="SetString" /> always moves a key to the end)
///     before every write, generous over the fixed per-page keys
///     (<c>AwaitingProgressModel</c>/<c>BrowseModel</c>) plus <c>WorkModel</c>'s one dynamic
///     per-leaf-id key, but still bounded regardless of how many leaves one session visits. A missing,
///     expired, tampered, or wrong-principal cookie is silently treated as empty -- exactly what an
///     absent session key already meant, never a thrown exception or a broken page.
/// </summary>
internal sealed class CookieFilterMemoryStore : IFilterMemoryStore
{
	internal const string CookieName = "JobTrack.Filters";
	internal const int MaxKeyCount = 32;
	internal const int MaxPayloadBytes = 2000;

	private const string ProtectorPurpose = "JobTrack.FilterMemory";
	private const int DefaultLifetimeHours = 8;
	private readonly List<KeyValuePair<string, string>> entries;

	private readonly HttpContext httpContext;
	private readonly TimeSpan lifetime;
	private readonly ITimeLimitedDataProtector protector;

	// A page's RecallFilters-style method can call SetString several times against one
	// CookieFilterMemoryStore instance within a single request. Response.Cookies.Append/Delete each
	// add a new Set-Cookie header rather than replacing an earlier one, so without this a client
	// would see one stale header per call instead of one header carrying the final state -- this
	// instance removes its own previous header before adding the next one, leaving exactly one.
	private string? previouslyAppendedSetCookieHeader;

	internal CookieFilterMemoryStore(HttpContext httpContext, IDataProtectionProvider dataProtectionProvider, TimeSpan? lifetime = null)
	{
		ArgumentNullException.ThrowIfNull(httpContext);
		ArgumentNullException.ThrowIfNull(dataProtectionProvider);

		this.httpContext = httpContext;
		this.lifetime = lifetime ?? TimeSpan.FromHours(DefaultLifetimeHours);

		// Binding the protector's purpose to the signed-in principal (Identity's own row id, stable
		// across a username rename) means a cookie left over from a different account -- or replayed
		// after PrincipalBoundSessionState.Reset deleted it -- fails Unprotect rather than ever being
		// read as someone else's remembered filters.
		var principalKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
		protector = dataProtectionProvider.CreateProtector(ProtectorPurpose, principalKey).ToTimeLimitedDataProtector();
		entries = Load();
	}

	public string? GetString(string key) => entries.Where(entry => entry.Key == key).Select(entry => entry.Value).FirstOrDefault();

	public void SetString(string key, string value)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(value);

		_ = entries.RemoveAll(entry => entry.Key == key);
		entries.Add(new(key, value));
		EnforceBounds();
		Persist();
	}

	public void Clear()
	{
		entries.Clear();
		Persist();
	}

	private void EnforceBounds()
	{
		while (entries.Count > MaxKeyCount) {
			entries.RemoveAt(0);
		}

		while (entries.Count > 0 && SerializedByteCount() > MaxPayloadBytes) {
			entries.RemoveAt(0);
		}
	}

	private int SerializedByteCount() => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entries));

	private void Persist()
	{
		RemovePreviouslyAppendedHeader();

		if (entries.Count == 0) {
			httpContext.Response.Cookies.Delete(CookieName);
		} else {
			var protectedValue = protector.Protect(JsonSerializer.Serialize(entries), lifetime);
			httpContext.Response.Cookies.Append(
				CookieName,
				protectedValue,
				new() {
					HttpOnly = true,
					Secure = true,
					SameSite = SameSiteMode.Lax,
					IsEssential = true,
					MaxAge = lifetime,
				});
		}

		var headers = httpContext.Response.Headers.SetCookie;
		previouslyAppendedSetCookieHeader = headers.Count > 0 ? headers[^1] : null;
	}

	private void RemovePreviouslyAppendedHeader()
	{
		var previous = previouslyAppendedSetCookieHeader;
		if (previous is null) {
			return;
		}

		httpContext.Response.Headers.SetCookie =
			new([.. httpContext.Response.Headers.SetCookie.Where(value => value != previous)]);
	}

	private List<KeyValuePair<string, string>> Load()
	{
		if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var cookieValue) || string.IsNullOrEmpty(cookieValue)) {
			return [];
		}

		try {
			return JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(protector.Unprotect(cookieValue)) ?? [];
		}
		catch (CryptographicException) {
			// Expired, tampered, or minted for a different principal -- same as no cookie at all.
			return [];
		}
		catch (JsonException) {
			return [];
		}
	}
}
