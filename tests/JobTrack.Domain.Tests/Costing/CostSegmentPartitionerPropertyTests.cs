namespace JobTrack.Domain.Tests.Costing;

using System.Numerics;
using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using Domain.Hierarchy;
using Domain.Intervals;
using FsCheck.Xunit;
using NodaTime;

public sealed class CostSegmentPartitionerPropertyTests
{
	private const int SampleTickCount = 32;
	private const int MaximumSessionCount = 12;
	private static readonly JobNodeId RootId = new(1);
	private static readonly Instant Epoch = Instant.FromUnixTimeTicks(0);
	private static readonly WorkInterval Bounds = new(Epoch, Epoch.PlusTicks(SampleTickCount));

	[Property(MaxTest = 200)]
	public void Partition_matches_an_independent_per_tick_membership_oracle(int[] starts, int[] lengths)
	{
		var (sessions, nodes) = Scenario(starts, lengths);

		var actual = CostSegmentPartitioner.Partition(sessions, [Bounds], nodes, [], [], Bounds);
		var expectedTicks = SampleWithOracle(sessions);

		foreach (var session in sessions) {
			var actualTicks = actual
							  .Where(allocation => allocation.SessionId == session.SessionId)
							  .Aggregate(Rational.Zero, (total, allocation) =>
								  total + new Rational(allocation.Share.SegmentTicks, allocation.Share.ConcurrencyDivisor));
			actualTicks.Should().Be(expectedTicks[session.SessionId]);
		}
	}

	[Property(MaxTest = 200)]
	public void Partition_is_independent_of_input_order(int[] starts, int[] lengths)
	{
		var (sessions, nodes) = Scenario(starts, lengths);

		var forwards = CostSegmentPartitioner.Partition(sessions, [Bounds], nodes, [], [], Bounds);
		var backwards = CostSegmentPartitioner.Partition(sessions.Reverse().ToArray(), [Bounds], nodes, [], [], Bounds);

		Canonical(forwards).Should().Equal(Canonical(backwards));
	}

	[Property(MaxTest = 200)]
	public void Every_segment_conserves_its_duration_exactly(int[] starts, int[] lengths)
	{
		var (sessions, nodes) = Scenario(starts, lengths);
		var allocations = CostSegmentPartitioner.Partition(sessions, [Bounds], nodes, [], [], Bounds);

		foreach (var segment in allocations.GroupBy(allocation => allocation.Segment)) {
			segment.Should().OnlyContain(allocation =>
				allocation.Share.SegmentTicks == segment.Key.Duration.BclCompatibleTicks
				&& allocation.Share.ConcurrencyDivisor == segment.Count());
		}
	}

	/// <summary>
	///     Eligible-piece discovery must not depend on the order the working intervals arrive in. The
	///     production ports always pass a sorted, disjoint normalized set, but
	///     <see cref="CostSegmentPartitioner.Partition" /> is public API and cannot assume it — this
	///     pins that guarantee across the sorted-scan optimisation.
	/// </summary>
	[Property(MaxTest = 200)]
	public void Partition_is_independent_of_working_interval_order(int[] starts, int[] lengths)
	{
		var (sessions, nodes) = Scenario(starts, lengths);
		var working = DisjointWorkingIntervals();

		var inOrder = CostSegmentPartitioner.Partition(sessions, working, nodes, [], [], Bounds);
		var reversed = CostSegmentPartitioner.Partition(sessions, working.Reverse().ToArray(), nodes, [], [], Bounds);

		Canonical(inOrder).Should().Equal(Canonical(reversed));
	}

	/// <summary>
	///     Overlapping (never-normalized) working intervals legitimately produce one eligible piece per
	///     overlapping interval, so the sorted-scan path must reproduce that exactly rather than
	///     silently de-duplicating it.
	/// </summary>
	[Property(MaxTest = 200)]
	public void Partition_preserves_overlapping_working_interval_pieces_in_any_order(int[] starts, int[] lengths)
	{
		var (sessions, nodes) = Scenario(starts, lengths);
		WorkInterval[] overlapping = [
			new(Epoch, Epoch.PlusTicks(20)),
			new(Epoch.PlusTicks(10), Epoch.PlusTicks(30)),
			new(Epoch.PlusTicks(5), Epoch.PlusTicks(SampleTickCount)),
		];

		var inOrder = CostSegmentPartitioner.Partition(sessions, overlapping, nodes, [], [], Bounds);
		var reversed = CostSegmentPartitioner.Partition(sessions, overlapping.Reverse().ToArray(), nodes, [], [], Bounds);

		Canonical(inOrder).Should().Equal(Canonical(reversed));
	}

	private static WorkInterval[] DisjointWorkingIntervals() => [
		new(Epoch.PlusTicks(1), Epoch.PlusTicks(6)),
		new(Epoch.PlusTicks(9), Epoch.PlusTicks(14)),
		new(Epoch.PlusTicks(18), Epoch.PlusTicks(25)),
		new(Epoch.PlusTicks(27), Epoch.PlusTicks(SampleTickCount)),
	];

	private static (CostableSession[] Sessions, Dictionary<JobNodeId, HierarchyNode> Nodes) Scenario(int[] starts, int[] lengths)
	{
		var count = Math.Min(Math.Min(starts.Length, lengths.Length), MaximumSessionCount);
		var sessions = Enumerable.Range(0, count)
								 .Select(index => {
									 var start = Math.Abs((long)starts[index]) % (SampleTickCount - 1);
									 var available = SampleTickCount - start;
									 var length = 1 + Math.Abs((long)lengths[index]) % available;
									 return new CostableSession(
										 new(index + 1),
										 new(index + 2),
										 new(Epoch.PlusTicks(start), Epoch.PlusTicks(start + length)));
								 })
								 .ToArray();
		var leaves = sessions
					 .Select(session => new HierarchyNode(session.NodeId, RootId, [], Achievement.InProgress))
					 .ToArray();
		var root = new HierarchyNode(RootId, null, [.. leaves.Select(leaf => leaf.Id)], null);
		return (sessions, leaves.Append(root).ToDictionary(node => node.Id));
	}

	private static Dictionary<WorkSessionId, Rational> SampleWithOracle(IReadOnlyCollection<CostableSession> sessions)
	{
		var totals = sessions.ToDictionary(session => session.SessionId, _ => Rational.Zero);
		for (var tick = 0; tick < SampleTickCount; ++tick) {
			var at = Epoch.PlusTicks(tick);
			var active = sessions.Where(session => session.Interval.Contains(at)).ToArray();
			foreach (var session in active) {
				totals[session.SessionId] += new Rational(1, active.Length);
			}
		}

		return totals;
	}

	private static IEnumerable<(WorkInterval Segment, long SessionId, long NodeId, long Ticks, int Divisor)> Canonical(
		IEnumerable<SessionSegmentAllocation> allocations) => allocations
															  .OrderBy(allocation => allocation.Segment.Start)
															  .ThenBy(allocation => allocation.SessionId.Value)
															  .Select(allocation => (
																  allocation.Segment,
																  allocation.SessionId.Value,
																  allocation.NodeId.Value,
																  allocation.Share.SegmentTicks,
																  allocation.Share.ConcurrencyDivisor));

	private readonly record struct Rational
	{
		public static readonly Rational Zero = new(BigInteger.Zero, BigInteger.One);

		public Rational(BigInteger numerator, BigInteger denominator)
		{
			var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
			Numerator = numerator / divisor;
			Denominator = denominator / divisor;
		}

		public BigInteger Numerator { get; }

		public BigInteger Denominator { get; }

		public static Rational operator +(Rational left, Rational right) => new(
			left.Numerator * right.Denominator + right.Numerator * left.Denominator,
			left.Denominator * right.Denominator);
	}
}
