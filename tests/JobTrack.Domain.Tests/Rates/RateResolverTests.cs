namespace JobTrack.Domain.Tests.Rates;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;
using Domain.Rates;
using Domain.Schedules;
using NodaTime;
using Schedules;

public sealed class RateResolverTests
{
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId BranchId = new(2);
	private static readonly JobNodeId LeafId = new(3);
	private static readonly Instant At = Instant.FromUtc(2024, 6, 1, 12, 0);

	private static HierarchyNode Leaf(JobNodeId id, JobNodeId? parentId) => new(id, parentId, [], Achievement.InProgress);

	private static Dictionary<JobNodeId, HierarchyNode> ThreeLevelTree()
	{
		var root = new HierarchyNode(RootId, null, [BranchId], null);
		var branch = new HierarchyNode(BranchId, RootId, [LeafId], null);
		var leaf = Leaf(LeafId, BranchId);
		return new() { [RootId] = root, [BranchId] = branch, [LeafId] = leaf };
	}

	[Fact]
	public void With_no_rate_source_available_resolution_throws_a_missing_rate_error()
	{
		var act = () =>
			RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [], [], [], null);

		act.Should().Throw<MissingRateException>();
	}

	[Fact]
	public void With_only_a_default_rate_it_is_used()
	{
		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [], [], [], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(20m), RateSource.UserDefault));
	}

	[Fact]
	public void An_effective_user_cost_rate_takes_precedence_over_the_default_rate()
	{
		var userRate = new UserCostRate(new(30m), At - Duration.FromDays(1), null);

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [], [], [userRate], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(30m), RateSource.UserCostRate));
	}

	[Fact]
	public void A_node_override_on_the_leaf_itself_takes_precedence_over_the_user_cost_rate()
	{
		var userRate = new UserCostRate(new(30m), At - Duration.FromDays(1), null);
		var leafOverride = new NodeRateOverride(LeafId, new(40m), At - Duration.FromDays(1), null);

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [], [leafOverride], [userRate], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(40m), RateSource.NodeOverride));
	}

	[Fact]
	public void A_node_override_on_an_ancestor_is_used_when_the_leaf_itself_has_none()
	{
		var branchOverride = new NodeRateOverride(BranchId, new(45m), At - Duration.FromDays(1), null);

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [], [branchOverride], [], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(45m), RateSource.NodeOverride));
	}

	[Fact]
	public void The_nearest_ancestor_override_wins_over_a_farther_one()
	{
		var branchOverride = new NodeRateOverride(BranchId, new(45m), At - Duration.FromDays(1), null);
		var rootOverride = new NodeRateOverride(RootId, new(60m), At - Duration.FromDays(1), null);

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [], [branchOverride, rootOverride], [], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(45m), RateSource.NodeOverride));
	}

	[Fact]
	public void An_ancestor_override_not_yet_effective_falls_through_to_a_farther_effective_one()
	{
		var branchOverride = new NodeRateOverride(BranchId, new(45m), At + Duration.FromDays(1), null);
		var rootOverride = new NodeRateOverride(RootId, new(60m), At - Duration.FromDays(1), null);

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [], [branchOverride, rootOverride], [], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(60m), RateSource.NodeOverride));
	}

	[Fact]
	public void A_priced_additive_exception_covering_the_instant_takes_precedence_over_a_node_override()
	{
		var leafOverride = new NodeRateOverride(LeafId, new(40m), At - Duration.FromDays(1), null);
		var exception = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(At - Duration.FromHours(1), At + Duration.FromHours(1)),
			new HourlyRate(99m));

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [exception], [leafOverride], [], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(99m), RateSource.OvertimeException));
	}

	[Fact]
	public void An_unpriced_additive_exception_does_not_affect_rate_resolution()
	{
		var leafOverride = new NodeRateOverride(LeafId, new(40m), At - Duration.FromDays(1), null);
		var exception = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(At - Duration.FromHours(1), At + Duration.FromHours(1)),
			null);

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [exception], [leafOverride], [], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(40m), RateSource.NodeOverride));
	}

	[Fact]
	public void A_priced_additive_exception_not_covering_the_instant_does_not_affect_rate_resolution()
	{
		var leafOverride = new NodeRateOverride(LeafId, new(40m), At - Duration.FromDays(1), null);
		var exception = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(At - Duration.FromHours(3), At - Duration.FromHours(2)),
			new HourlyRate(99m));

		var resolved = RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [exception], [leafOverride], [], new HourlyRate(20m));

		resolved.Should().Be(new ResolvedRate(new(40m), RateSource.NodeOverride));
	}

	[Fact]
	public void Resolve_throws_for_an_unknown_effect_value()
	{
		var exception = ScheduleExceptionEntryTestSupport.WithEffect(
			(ScheduleExceptionEffect)(-1),
			new(At - Duration.FromHours(1), At + Duration.FromHours(1)),
			new HourlyRate(99m));

		var act = () => RateResolver.Resolve(LeafId, At, ThreeLevelTree(), [exception], [], [], new HourlyRate(20m));

		act.Should().Throw<ArgumentOutOfRangeException>();
	}
}
