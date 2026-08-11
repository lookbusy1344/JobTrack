namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

public sealed class OwnershipFilterTests
{
	private static readonly AppUserId Alice = new(100);
	private static readonly AppUserId Bob = new(200);

	public sealed class All
	{
		[Fact]
		public void Matches_an_unassigned_node() => OwnershipFilter.All.Matches(null).Should().BeTrue();

		[Fact]
		public void Matches_any_owned_node() => OwnershipFilter.All.Matches(Alice).Should().BeTrue();
	}

	public sealed class Unassigned
	{
		[Fact]
		public void Matches_an_unassigned_node() => OwnershipFilter.Unassigned.Matches(null).Should().BeTrue();

		[Fact]
		public void Does_not_match_an_owned_node() => OwnershipFilter.Unassigned.Matches(Alice).Should().BeFalse();
	}

	public sealed class OwnedBy
	{
		[Fact]
		public void Matches_the_named_owner() => OwnershipFilter.OwnedBy(Alice).Matches(Alice).Should().BeTrue();

		[Fact]
		public void Does_not_match_a_different_owner() => OwnershipFilter.OwnedBy(Alice).Matches(Bob).Should().BeFalse();

		[Fact]
		public void Does_not_match_an_unassigned_node() => OwnershipFilter.OwnedBy(Alice).Matches(null).Should().BeFalse();
	}
}
