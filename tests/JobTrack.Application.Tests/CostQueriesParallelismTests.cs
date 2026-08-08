namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Intervals;
using NodaTime;

/// <summary>
///     Large-database performance plan §4 Stage 1's worker-orchestration correctness properties,
///     retained after the parallel candidate was withdrawn on concurrent-load evidence: deterministic
///     aggregation, cancellation after materialization, and direct exception propagation through the
///     sole exception failure channel.
/// </summary>
public sealed class CostQueriesWorkerOrchestrationTests
{
	private const int FewWorkerCount = 3;
	private const int ManyWorkerCount = 7;
	private static readonly AppUserId CostViewerId = new(1);
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId BranchId = new(2);
	private static readonly JobNodeId LeafId = new(3);

	private static readonly WorkInterval FullDay = new(At(0), At(24));

	private static Instant At(int hour) => hour == 24 ? Instant.FromUtc(2026, 1, 2, 0, 0) : Instant.FromUtc(2026, 1, 1, hour, 0);

	private static Instant At(int hour, int minute) => Instant.FromUtc(2026, 1, 1, hour, minute);

	private static FakeCostQueryPort CreatePortWithNodes()
	{
		var port = new FakeCostQueryPort();
		port.SeedRoles(CostViewerId, EmployeeRole.CostViewer);
		port.SeedNode(new(RootId, null, [BranchId], null));
		port.SeedNode(new(BranchId, RootId, [LeafId], null));
		port.SeedNode(new(LeafId, BranchId, [], Achievement.InProgress));
		return port;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	/// <summary>Seeds <paramref name="workerCount" /> workers, each with one non-overlapping ten-minute session on <see cref="LeafId" />.</summary>
	private static void SeedIndependentTenMinuteWorkers(FakeCostQueryPort port, int workerCount)
	{
		for (var i = 0; i < workerCount; ++i) {
			port.SeedWorker(new() {
				Sessions = [new(new(1000 + i), LeafId, new(At(10, 0), At(10, 10)))],
				EffectiveWorkingIntervals = [FullDay],
				ScheduledWorkingIntervals = [FullDay],
				Exceptions = [],
				NodeOverrides = [],
				UserCostRates = [],
				UserDefaultRate = new HourlyRate(60m),
			});
		}
	}

	[Fact]
	public async Task Hierarchy_totals_with_many_workers_sums_every_independent_workers_time()
	{
		var port = CreatePortWithNodes();
		var workerCount = ManyWorkerCount;
		SeedIndependentTenMinuteWorkers(port, workerCount);
		var sut = new CostQueries(port);

		var result = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });

		// Each worker's ten minutes never overlaps another worker's own sessions, so nothing divides:
		// workerCount * 10 minutes at £60/hr.
		result.ExactCosts[LeafId].Should().Be(new(workerCount * 10m));
		// ToHours() rounds to six decimal places (midpoint-to-even, the exact duration reporting
		// boundary) -- workerCount=7 * 10 minutes = 70 minutes = 1.1666666... hours -> 1.166667.
		result.AllocatedDurations[LeafId].ToHours().Should().Be(1.166667m);
	}

	[Fact]
	public async Task Hierarchy_totals_with_few_workers_sums_every_independent_workers_time()
	{
		var port = CreatePortWithNodes();
		var workerCount = FewWorkerCount;
		SeedIndependentTenMinuteWorkers(port, workerCount);
		var sut = new CostQueries(port);

		var result = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });

		result.ExactCosts[LeafId].Should().Be(new(workerCount * 10m));
	}

	/// <summary>
	///     Stage 1's non-negotiable determinism requirement: identical inputs must produce byte-identical
	///     output. Repeated calls over a staggered worker set must all agree exactly, not merely on average.
	/// </summary>
	[Fact]
	public async Task Hierarchy_totals_are_identical_across_repeated_calls()
	{
		var port = CreatePortWithNodes();
		var workerCount = ManyWorkerCount + 5;
		for (var i = 0; i < workerCount; ++i) {
			// Cross-worker staggering varies segment boundaries while preserving ADR 0017's
			// per-worker concurrency divisor.
			var startMinute = i % 10;
			port.SeedWorker(new() {
				Sessions = [new(new(2000 + i), LeafId, new(At(10, startMinute), At(10, startMinute + 10)))],
				EffectiveWorkingIntervals = [FullDay],
				ScheduledWorkingIntervals = [FullDay],
				Exceptions = [],
				NodeOverrides = [],
				UserCostRates = [],
				UserDefaultRate = new HourlyRate(60m),
			});
		}

		var sut = new CostQueries(port);

		var first = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });
		for (var attempt = 0; attempt < 15; ++attempt) {
			var repeat = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });
			repeat.ExactCosts[LeafId].Should().Be(first.ExactCosts[LeafId]);
			repeat.AllocatedDurations[LeafId].Should().Be(first.AllocatedDurations[LeafId]);
		}
	}

	[Fact]
	public async Task Hierarchy_totals_propagate_a_workers_invariant_violation_directly_not_wrapped()
	{
		var port = CreatePortWithNodes();
		var workerCount = ManyWorkerCount;
		SeedIndependentTenMinuteWorkers(port, workerCount);
		// One poisoned worker: two of their own sessions overlap on the same leaf, which
		// CostSegmentPartitioner.ValidateNoSameLeafOverlap forbids regardless of any other worker.
		port.SeedWorker(new() {
			Sessions = [
				new(new(9001), LeafId, new(At(9, 0), At(9, 30))),
				new(new(9002), LeafId, new(At(9, 15), At(9, 45))),
			],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var act = () => sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });

		await act.Should().ThrowAsync<InvariantViolationException>();
	}

	[Fact]
	public async Task Hierarchy_totals_with_many_workers_honor_an_already_cancelled_token()
	{
		var port = CreatePortWithNodes();
		SeedIndependentTenMinuteWorkers(port, ManyWorkerCount);
		var sut = new CostQueries(port);
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		var act = () => sut.GetHierarchyTotalsAsync(
			new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) }, cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task GetRequesterVisibleHierarchyAsync_at_or_above_the_parallel_threshold_sums_every_independent_workers_duration()
	{
		var port = CreatePortWithNodes();
		var workerCount = ManyWorkerCount;
		SeedIndependentTenMinuteWorkers(port, workerCount);
		var sut = new CostQueries(port);

		var result = await sut.GetRequesterVisibleHierarchyAsync(BranchId, At(24));

		result[LeafId].ToHours().Should().Be(1.166667m);
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_with_many_workers_honors_cancellation_after_materialization()
	{
		using var cancellation = new CancellationTokenSource();
		var port = new FakeCostQueryPort { AfterGetBulkCostInputs = cancellation.Cancel };
		port.SeedRoles(CostViewerId, EmployeeRole.CostViewer);
		port.SeedNode(new(RootId, null, [BranchId], null));
		port.SeedNode(new(BranchId, RootId, [LeafId], null));
		port.SeedNode(new(LeafId, BranchId, [], Achievement.InProgress));
		SeedIndependentTenMinuteWorkers(port, ManyWorkerCount);
		var sut = new CostQueries(port);

		var act = () => sut.GetBulkNodeCostsAsync(
			new() { Context = ContextFor(CostViewerId), NodeIds = [BranchId], AsOf = At(24) }, cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task GetHierarchyTotalsAsync_honors_cancellation_after_materialization()
	{
		using var cancellation = new CancellationTokenSource();
		var port = CreatePortWithNodes();
		port.AfterGetCostInputs = cancellation.Cancel;
		SeedIndependentTenMinuteWorkers(port, ManyWorkerCount);
		var sut = new CostQueries(port);

		var act = () => sut.GetHierarchyTotalsAsync(
			new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) }, cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task GetCostDetailsAsync_honors_cancellation_after_materialization()
	{
		using var cancellation = new CancellationTokenSource();
		var port = CreatePortWithNodes();
		port.AfterGetCostInputs = cancellation.Cancel;
		SeedIndependentTenMinuteWorkers(port, ManyWorkerCount);
		var sut = new CostQueries(port);

		var act = () => sut.GetCostDetailsAsync(
			new() { Context = ContextFor(CostViewerId), NodeId = LeafId, AsOf = At(24) }, cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task GetRequesterVisibleHierarchyAsync_honors_cancellation_after_materialization()
	{
		using var cancellation = new CancellationTokenSource();
		var port = CreatePortWithNodes();
		port.AfterGetCostInputs = cancellation.Cancel;
		SeedIndependentTenMinuteWorkers(port, ManyWorkerCount);
		var sut = new CostQueries(port);

		var act = () => sut.GetRequesterVisibleHierarchyAsync(BranchId, At(24), cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}
}
