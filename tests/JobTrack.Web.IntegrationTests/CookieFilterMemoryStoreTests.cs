namespace JobTrack.Web.IntegrationTests;

using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

/// <summary>
///     ADR 0066 Stage 3 (docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.3): unit
///     coverage for <see cref="CookieFilterMemoryStore" />'s own cookie mechanics -- absent/empty
///     values, corruption, expiry, the payload/key-count bounds, principal binding, and cookie
///     options -- complementing the page-level `Resolve`/`ResolveFlag`/`ResolveText` behaviour already
///     proven end to end by <c>AwaitingProgressTests</c>/<c>JobBrowseNavigationTests</c>/
///     <c>LeafWorkTests</c>, and the cross-host recall proven by
///     <c>TwoHostPostgreSqlAcceptanceTests</c>. Two independent stores against a shared
///     <see cref="EphemeralDataProtectionProvider" /> stand in for the request/response round trip: the
///     first store's <see cref="HttpResponse" /> Set-Cookie header becomes the second store's
///     <see cref="HttpRequest" /> Cookie header, exactly as a browser would replay it.
/// </summary>
public sealed class CookieFilterMemoryStoreTests
{
	private const string PrincipalKey = "42";
	private const string OtherPrincipalKey = "99";
	private const string Key = "Jobs.Test.Key";

	private static readonly EphemeralDataProtectionProvider Provider = new();

	[Fact]
	public void GetString_returns_null_when_no_cookie_is_present()
	{
		var store = CreateStore(CreateContext(PrincipalKey));

		store.GetString(Key).Should().BeNull();
	}

	[Fact]
	public void A_value_set_through_one_request_is_readable_through_a_fresh_store_replaying_the_response_cookie()
	{
		var first = CreateContext(PrincipalKey);
		CreateStore(first).SetString(Key, "oak");

		var second = CreateStore(ReplayCookies(first, PrincipalKey));

		second.GetString(Key).Should().Be("oak");
	}

	[Fact]
	public void An_empty_value_round_trips_as_a_distinct_empty_string_not_absent()
	{
		var first = CreateContext(PrincipalKey);
		CreateStore(first).SetString(Key, string.Empty);

		var second = CreateStore(ReplayCookies(first, PrincipalKey));

		second.GetString(Key).Should().Be(string.Empty);
		second.GetString(Key).Should().NotBeNull();
	}

	[Fact]
	public void A_tampered_cookie_value_is_treated_as_absent_not_thrown()
	{
		var first = CreateContext(PrincipalKey);
		CreateStore(first).SetString(Key, "oak");
		var replayed = ReplayCookies(first, PrincipalKey);
		replayed.Request.Headers.Cookie = new($"{CookieFilterMemoryStore.CookieName}=not-a-real-protected-value");

		var act = () => CreateStore(replayed).GetString(Key);

		act.Should().NotThrow();
		act().Should().BeNull();
	}

	[Fact]
	public void An_expired_cookie_is_treated_as_absent()
	{
		var first = CreateContext(PrincipalKey);
		new CookieFilterMemoryStore(first, Provider, TimeSpan.FromMilliseconds(1)).SetString(Key, "oak");
		Thread.Sleep(50);

		var second = CreateStore(ReplayCookies(first, PrincipalKey));

		second.GetString(Key).Should().BeNull();
	}

	[Fact]
	public void A_cookie_minted_for_one_principal_is_not_readable_through_a_store_bound_to_a_different_principal()
	{
		var first = CreateContext(PrincipalKey);
		CreateStore(first).SetString(Key, "oak");

		var second = CreateStore(ReplayCookies(first, OtherPrincipalKey));

		second.GetString(Key).Should().BeNull();
	}

	[Fact]
	public void Setting_more_than_the_key_count_bound_evicts_the_least_recently_set_entry()
	{
		var context = CreateContext(PrincipalKey);
		var store = CreateStore(context);
		for (var i = 0; i < CookieFilterMemoryStore.MaxKeyCount + 1; ++i) {
			store.SetString($"key-{i}", "v");
		}

		var reloaded = CreateStore(ReplayCookies(context, PrincipalKey));

		reloaded.GetString("key-0").Should().BeNull("the oldest entry must be evicted once the key-count bound is exceeded");
		reloaded.GetString($"key-{CookieFilterMemoryStore.MaxKeyCount}").Should().Be("v", "the most recently set entry must survive");
	}

	[Fact]
	public void Setting_a_value_that_pushes_the_payload_past_its_byte_bound_evicts_the_oldest_entries()
	{
		var context = CreateContext(PrincipalKey);
		var store = CreateStore(context);
		store.SetString("first", "small");
		// Long enough that adding it alongside "first" pushes the payload past the bound, but short
		// enough to fit the bound by itself once "first" is evicted -- otherwise both would be
		// evicted and the test would not distinguish "oldest evicted" from "everything evicted".
		store.SetString("second", new('x', CookieFilterMemoryStore.MaxPayloadBytes - 50));

		var reloaded = CreateStore(ReplayCookies(context, PrincipalKey));

		reloaded.GetString("first").Should().BeNull("the oldest entry must be evicted once the payload-size bound is exceeded");
		reloaded.GetString("second").Should().NotBeNull();
	}

	[Fact]
	public void Persisting_a_value_sets_HttpOnly_Secure_SameSiteLax_and_a_bounded_MaxAge()
	{
		var context = CreateContext(PrincipalKey);
		CreateStore(context).SetString(Key, "oak");

		var setCookie = context.Response.Headers.SetCookie.Should().ContainSingle().Subject;
		setCookie.Should().ContainEquivalentOf("httponly");
		setCookie.Should().ContainEquivalentOf("secure");
		setCookie.Should().ContainEquivalentOf("samesite=lax");
		setCookie.Should().Contain("max-age=");
	}

	[Fact]
	public void Clear_deletes_the_cookie_rather_than_persisting_an_empty_payload()
	{
		var context = CreateContext(PrincipalKey);
		var store = CreateStore(context);
		store.SetString(Key, "oak");

		store.Clear();

		// Exactly one Set-Cookie header survives -- Clear replaces SetString's own persisted header
		// rather than adding a second one alongside it.
		var setCookie = context.Response.Headers.SetCookie.Should().ContainSingle().Subject;
		setCookie.Should().StartWith($"{CookieFilterMemoryStore.CookieName}=;");
	}

	private static CookieFilterMemoryStore CreateStore(HttpContext context) => new(context, Provider);

	private static DefaultHttpContext CreateContext(string principalKey)
	{
		var context = new DefaultHttpContext();
		context.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, principalKey)]));
		return context;
	}

	/// <summary>
	///     Carries <paramref name="responseContext" />'s Set-Cookie header into a fresh request, as a
	///     browser would. Every <see cref="CookieFilterMemoryStore.SetString" /> call persists the
	///     entire current entry list and appends its own Set-Cookie header rather than replacing an
	///     earlier one from the same request/response, so the *last* header (not the first) carries
	///     the cumulative state a real browser would end up applying.
	/// </summary>
	private static DefaultHttpContext ReplayCookies(HttpContext responseContext, string principalKey)
	{
		var context = CreateContext(principalKey);
		if (responseContext.Response.Headers.SetCookie.Count > 0) {
			var pair = responseContext.Response.Headers.SetCookie[^1]!.Split(';')[0];
			context.Request.Headers.Cookie = new(pair);
		}

		return context;
	}
}
