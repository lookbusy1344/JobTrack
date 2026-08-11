namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;
using NodaTime;

public sealed class PersonalAccessTokenPolicyTests
{
	private const string ExpiryNotInFutureConstraint = "personal-access-token-expiry-not-in-future";
	private const string ExpiryTooLongConstraint = "personal-access-token-expiry-too-long";
	private static readonly Instant Now = Instant.FromUtc(2026, 1, 1, 0, 0, 0);

	[Fact]
	public void A_future_expiry_within_the_maximum_lifetime_is_accepted()
	{
		var act = () => PersonalAccessTokenPolicy.EnsureValidExpiry(Now, Now + Duration.FromDays(30));

		act.Should().NotThrow();
	}

	[Fact]
	public void An_expiry_exactly_at_now_is_rejected_as_not_in_the_future()
	{
		var act = () => PersonalAccessTokenPolicy.EnsureValidExpiry(Now, Now);

		act.Should().Throw<InvariantViolationException>()
			.Which.ConstraintId.Should().Be(ExpiryNotInFutureConstraint);
	}

	[Fact]
	public void An_expiry_before_now_is_rejected_as_not_in_the_future()
	{
		var act = () => PersonalAccessTokenPolicy.EnsureValidExpiry(Now, Now - Duration.FromMinutes(1));

		act.Should().Throw<InvariantViolationException>()
			.Which.ConstraintId.Should().Be(ExpiryNotInFutureConstraint);
	}

	[Fact]
	public void An_expiry_exactly_at_the_maximum_lifetime_is_accepted()
	{
		// Boundary: expiresAt - now == MaxLifetime must be allowed (kills the `>`->`>=` mutant).
		var act = () => PersonalAccessTokenPolicy.EnsureValidExpiry(Now, Now + PersonalAccessTokenPolicy.MaxLifetime);

		act.Should().NotThrow();
	}

	[Fact]
	public void An_expiry_beyond_the_maximum_lifetime_is_rejected_as_too_long()
	{
		var expiresAt = Now + PersonalAccessTokenPolicy.MaxLifetime + Duration.FromSeconds(1);

		var act = () => PersonalAccessTokenPolicy.EnsureValidExpiry(Now, expiresAt);

		act.Should().Throw<InvariantViolationException>()
			.Which.ConstraintId.Should().Be(ExpiryTooLongConstraint);
	}
}
