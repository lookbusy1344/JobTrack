namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using NodaTime;

/// <summary>Round-trip coverage for ADR 0057's <see cref="AuthenticationProperties.Items" />-backed timestamps.</summary>
public sealed class SessionAuthenticationInstantsTests
{
	private static readonly Instant Origin = Instant.FromUtc(2026, 8, 1, 9, 0, 0);
	private static readonly Instant Recent = Instant.FromUtc(2026, 8, 1, 12, 30, 0);

	[Fact]
	public void Stamp_round_trips_both_instants()
	{
		var properties = new AuthenticationProperties();

		SessionAuthenticationInstants.Stamp(properties, Origin, Recent);

		SessionAuthenticationInstants.TryGetOrigin(properties).Should().Be(Origin);
		SessionAuthenticationInstants.TryGetRecentAuthentication(properties).Should().Be(Recent);
	}

	[Fact]
	public void Missing_properties_yield_no_instants()
	{
		SessionAuthenticationInstants.TryGetOrigin(null).Should().BeNull();
		SessionAuthenticationInstants.TryGetRecentAuthentication(null).Should().BeNull();
	}

	[Fact]
	public void Unstamped_properties_yield_no_instants()
	{
		var properties = new AuthenticationProperties();

		SessionAuthenticationInstants.TryGetOrigin(properties).Should().BeNull();
		SessionAuthenticationInstants.TryGetRecentAuthentication(properties).Should().BeNull();
	}

	[Fact]
	public void Corrupt_item_value_yields_no_instant()
	{
		var properties = new AuthenticationProperties();
		properties.Items["jt.origin"] = "not-a-number";

		SessionAuthenticationInstants.TryGetOrigin(properties).Should().BeNull();
	}

	[Fact]
	public void Restamping_advances_recent_but_can_preserve_origin()
	{
		var properties = new AuthenticationProperties();
		SessionAuthenticationInstants.Stamp(properties, Origin, Origin);

		var laterRecent = Recent;
		SessionAuthenticationInstants.Stamp(properties, Origin, laterRecent);

		SessionAuthenticationInstants.TryGetOrigin(properties).Should().Be(Origin);
		SessionAuthenticationInstants.TryGetRecentAuthentication(properties).Should().Be(laterRecent);
	}
}
