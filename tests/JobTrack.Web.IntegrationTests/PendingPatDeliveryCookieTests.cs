namespace JobTrack.Web.IntegrationTests;

using Abstractions;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

/// <summary>
///     ADR 0066 Stage 4 (docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.5): unit
///     coverage for <see cref="PendingPatDeliveryCookie" />'s own cookie mechanics -- actor binding,
///     expiry, one-display, tamper, wrong-actor, and the pre-issuance payload bound -- complementing
///     the end-to-end PRG proof in <c>PersonalAccessTokenManagementTests</c> and the cross-host
///     delivery proven by <c>TwoHostPostgreSqlAcceptanceTests</c>. Two independent calls against a
///     shared <see cref="EphemeralDataProtectionProvider" /> stand in for the request/response round
///     trip, mirroring <see cref="CookieFilterMemoryStoreTests" />.
/// </summary>
public sealed class PendingPatDeliveryCookieTests
{
	private static readonly AppUserId Actor = new(42);
	private static readonly AppUserId OtherActor = new(99);
	private static readonly EphemeralDataProtectionProvider Provider = new();

	[Fact]
	public void TryConsume_returns_false_when_no_cookie_is_present()
	{
		var consumed = PendingPatDeliveryCookie.TryConsume(new DefaultHttpContext(), Provider, Actor, out _, out _);

		consumed.Should().BeFalse();
	}

	[Fact]
	public void A_published_token_is_readable_through_a_fresh_request_replaying_the_response_cookie()
	{
		var published = new DefaultHttpContext();
		PendingPatDeliveryCookie.Publish(published, Provider, Actor, "ci-token", "jtpat_secret");

		var consumed = PendingPatDeliveryCookie.TryConsume(ReplayCookie(published), Provider, Actor, out var label, out var plaintext);

		consumed.Should().BeTrue();
		label.Should().Be("ci-token");
		plaintext.Should().Be("jtpat_secret");
	}

	[Fact]
	public void Consuming_deletes_the_cookie_so_a_compliant_client_never_displays_it_twice()
	{
		var published = new DefaultHttpContext();
		PendingPatDeliveryCookie.Publish(published, Provider, Actor, "ci-token", "jtpat_secret");
		var firstRequest = ReplayCookie(published);

		var firstConsume = PendingPatDeliveryCookie.TryConsume(firstRequest, Provider, Actor, out _, out _);
		// A compliant client honors the deletion this response just set and stops sending the
		// cookie -- exactly what ReplayCookie's own "no Set-Cookie means nothing to carry forward"
		// path does, matching CookieFilterMemoryStoreTests' reset-boundary test.
		var secondConsume = PendingPatDeliveryCookie.TryConsume(ReplayCookie(firstRequest), Provider, Actor, out _, out _);

		firstConsume.Should().BeTrue();
		secondConsume.Should().BeFalse();
	}

	[Fact]
	public void A_tampered_cookie_value_is_treated_as_absent_not_thrown()
	{
		var published = new DefaultHttpContext();
		PendingPatDeliveryCookie.Publish(published, Provider, Actor, "ci-token", "jtpat_secret");
		var replayed = ReplayCookie(published);
		replayed.Request.Headers.Cookie = new($"{PendingPatDeliveryCookie.CookieName}=not-a-real-protected-value");

		var act = () => PendingPatDeliveryCookie.TryConsume(replayed, Provider, Actor, out _, out _);

		act.Should().NotThrow();
		act().Should().BeFalse();
	}

	[Fact]
	public void An_expired_cookie_is_treated_as_absent()
	{
		var published = new DefaultHttpContext();
		PendingPatDeliveryCookie.Publish(published, Provider, Actor, "ci-token", "jtpat_secret", TimeSpan.FromMilliseconds(1));
		Thread.Sleep(50);

		var consumed = PendingPatDeliveryCookie.TryConsume(ReplayCookie(published), Provider, Actor, out _, out _);

		consumed.Should().BeFalse();
	}

	[Fact]
	public void A_token_published_for_one_actor_is_not_consumable_by_a_different_actor()
	{
		var published = new DefaultHttpContext();
		PendingPatDeliveryCookie.Publish(published, Provider, Actor, "ci-token", "jtpat_secret");

		var consumed = PendingPatDeliveryCookie.TryConsume(ReplayCookie(published), Provider, OtherActor, out _, out _);

		consumed.Should().BeFalse();
	}

	[Fact]
	public void A_wrong_actor_attempt_leaves_the_cookie_intact_for_the_real_owner()
	{
		var published = new DefaultHttpContext();
		PendingPatDeliveryCookie.Publish(published, Provider, Actor, "ci-token", "jtpat_secret");

		var wrongActorConsume = PendingPatDeliveryCookie.TryConsume(ReplayCookie(published), Provider, OtherActor, out _, out _);
		// The wrong-actor attempt above never deletes the cookie (CryptographicException, no
		// Set-Cookie appended), so the real owner replaying the *original* published cookie still
		// finds it pending.
		var realOwnerConsume = PendingPatDeliveryCookie.TryConsume(ReplayCookie(published), Provider, Actor, out var label, out var plaintext);

		wrongActorConsume.Should().BeFalse();
		realOwnerConsume.Should().BeTrue("a wrong-actor guess must not destroy the real owner's still-pending delivery");
		label.Should().Be("ci-token");
		plaintext.Should().Be("jtpat_secret");
	}

	[Fact]
	public void CanDeliver_accepts_a_label_at_the_model_binding_length_limit()
	{
		// PersonalAccessTokensModel.IssueTokenInput.Label is bounded to 200 code points; the worst
		// case (4-byte UTF-8 code points throughout, e.g. an emoji -- too wide for a single `char`,
		// hence the string literal repeated rather than `new string(char, count)`) must still fit.
		var worstCaseLabel = string.Concat(Enumerable.Repeat("\U0001F600", 200));

		PendingPatDeliveryCookie.CanDeliver(worstCaseLabel).Should().BeTrue();
	}

	[Fact]
	public void CanDeliver_refuses_a_label_that_would_not_fit_the_payload_bound()
	{
		var oversizedLabel = new string('x', PendingPatDeliveryCookie.MaxPayloadBytes);

		PendingPatDeliveryCookie.CanDeliver(oversizedLabel).Should().BeFalse();
	}

	/// <summary>Carries the response's Set-Cookie header into a fresh request, as a browser would -- absent if none was set.</summary>
	private static DefaultHttpContext ReplayCookie(HttpContext responseContext)
	{
		var context = new DefaultHttpContext();
		if (responseContext.Response.Headers.SetCookie.Count > 0) {
			var pair = responseContext.Response.Headers.SetCookie[^1]!.Split(';')[0];
			context.Request.Headers.Cookie = new(pair);
		}

		return context;
	}
}
