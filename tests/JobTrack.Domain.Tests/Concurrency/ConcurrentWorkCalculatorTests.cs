namespace JobTrack.Domain.Tests.Concurrency;

using Abstractions;
using AwesomeAssertions;
using Domain.Concurrency;
using Domain.Intervals;
using NodaTime;

public sealed class ConcurrentWorkCalculatorTests
{
	private static readonly JobNodeId Subject = new(100);

	private static readonly AppUserId Alice = new(1);

	private static readonly AppUserId Bob = new(2);

	private static Instant At(int hour) => Instant.FromUtc(2026, 3, 2, hour, 0);

	private static WorkInterval Between(int startHour, int endHour) => new(At(startHour), At(endHour));

	private static ConcurrentWorkSession Session(long id, AppUserId worker, long nodeId, int startHour, int endHour) =>
		new(new WorkSessionId(id), new JobNodeId(nodeId), worker, Between(startHour, endHour));

	public sealed class Overlaps
	{
		[Fact]
		public void A_worker_with_no_concurrent_session_produces_no_row()
		{
			var result = ConcurrentWorkCalculator.Calculate([Session(1, Alice, Subject.Value, 9, 12)], []);

			result.Should().BeEmpty();
		}

		[Fact]
		public void Sessions_that_merely_touch_do_not_intersect()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 11)],
				[Session(2, Alice, 200, 11, 13)]);

			result.Should().BeEmpty();
		}

		[Fact]
		public void One_shared_hour_is_reported_as_one_hour_of_overlap()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 12)],
				[Session(2, Alice, 200, 11, 13)]);

			result.Should().ContainSingle().Which.Should().Be(new ConcurrentWorkOverlap(
				Alice, new JobNodeId(200), Duration.FromHours(1), 1, At(11), At(12)));
		}

		[Fact]
		public void Another_workers_session_never_intersects_this_workers()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 17)],
				[Session(2, Bob, 200, 9, 17)]);

			result.Should().BeEmpty();
		}

		[Fact]
		public void A_session_on_the_subject_node_itself_is_not_a_concurrent_job()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 12)],
				[Session(2, Alice, Subject.Value, 9, 12)]);

			result.Should().BeEmpty();
		}

		[Fact]
		public void Overlaps_with_the_same_node_accumulate_into_one_row()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 11), Session(2, Alice, Subject.Value, 14, 16)],
				[Session(3, Alice, 200, 10, 15)]);

			result.Should().ContainSingle().Which.Should().Be(new ConcurrentWorkOverlap(
				Alice, new JobNodeId(200), Duration.FromHours(2), 2, At(10), At(15)));
		}

		[Fact]
		public void Each_concurrent_node_gets_its_own_row()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 17)],
				[Session(2, Alice, 200, 9, 12), Session(3, Alice, 300, 16, 17)]);

			result.Should().Equal(
				new ConcurrentWorkOverlap(Alice, new JobNodeId(200), Duration.FromHours(3), 1, At(9), At(12)),
				new ConcurrentWorkOverlap(Alice, new JobNodeId(300), Duration.FromHours(1), 1, At(16), At(17)));
		}
	}

	public sealed class Ordering
	{
		[Fact]
		public void Each_workers_rows_are_contiguous_and_ordered_by_descending_overlap()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 17), Session(2, Bob, Subject.Value, 9, 17)],
				[
					Session(3, Alice, 200, 9, 11),
					Session(4, Alice, 300, 9, 15),
					Session(5, Bob, 400, 9, 10),
				]);

			result.Select(row => (row.WorkedByUserId, row.NodeId)).Should().Equal(
				(Alice, new JobNodeId(300)),
				(Alice, new JobNodeId(200)),
				(Bob, new JobNodeId(400)));
		}

		[Fact]
		public void The_worker_with_the_most_concurrent_time_leads()
		{
			var result = ConcurrentWorkCalculator.Calculate(
				[Session(1, Alice, Subject.Value, 9, 17), Session(2, Bob, Subject.Value, 9, 17)],
				[Session(3, Alice, 200, 9, 10), Session(4, Bob, 400, 9, 16)]);

			result.Select(row => row.WorkedByUserId).Should().Equal(Bob, Alice);
		}
	}
}
