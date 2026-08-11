namespace JobTrack.Application.Tests;

using System.Text.RegularExpressions;
using Abstractions;
using AwesomeAssertions;
using Domain.Intervals;
using NodaTime;

/// <summary>
///     Large-database performance plan §4 Stage 5b: the cost-read path logs one compact structured
///     line per call (`cost_read_growth_signal`) recording DB-vs-engine duration split, cost-window
///     span, contributing worker count, total session count and max/p50/p95 sessions per worker --
///     the Stage 3 trigger-decision input. Post-1.0 plan §Stage 2's redaction rule binds: durations
///     and counts only, never identities, rates or costs.
/// </summary>
public sealed partial class CostQueriesGrowthSignalTests
{
	private static readonly AppUserId CostViewerId = new(1);
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId BranchId = new(2);
	private static readonly JobNodeId LeafId = new(3);

	private static readonly WorkInterval FullDay = new(At(0), At(24));

	// The template's own field order and shape -- proves nothing else (a rate, a cost amount, a node
	// or user id) reaches the line, rather than merely checking for the absence of one guessed value.
	[GeneratedRegex(
		@"^cost_read_growth_signal operation=\w+ db_ms=\d+ engine_ms=\d+ window_ticks=\d+ workers=\d+ " +
		@"sessions_total=\d+ sessions_max=\d+ sessions_p50=\d+ sessions_p95=\d+$")]
	private static partial Regex GrowthSignalLinePattern();

	private static Instant At(int hour) => hour == 24 ? Instant.FromUtc(2026, 1, 2, 0, 0) : Instant.FromUtc(2026, 1, 1, hour, 0);

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

	private static void AssertGrowthSignalValues(CapturingLogger<CostQueries> logger, string operation)
	{
		var entry = logger.Entries.Should().ContainSingle().Subject;
		entry.Properties["Operation"].Should().NotBeNull();
		entry.Properties["Operation"]!.ToString().Should().Be(operation);
		entry.Properties["WindowTicks"].Should().Be(new WorkInterval(Instant.MinValue, At(24)).Duration.BclCompatibleTicks);
		entry.Properties["Workers"].Should().Be(1);
		entry.Properties["SessionsTotal"].Should().Be(1);
		entry.Properties["SessionsMax"].Should().Be(1);
		entry.Properties["SessionsP50"].Should().Be(1);
		entry.Properties["SessionsP95"].Should().Be(1);
	}

	[Fact]
	public async Task GetHierarchyTotalsAsync_logs_a_growth_signal_matching_only_the_compact_field_template()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(new(1), LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var logger = new CapturingLogger<CostQueries>();
		var sut = new CostQueries(port, logger);

		_ = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });

		var growthSignalLines = logger.Messages.Where(message => message.StartsWith("cost_read_growth_signal", StringComparison.Ordinal)).ToArray();
		growthSignalLines.Should().ContainSingle();
		growthSignalLines[0].Should().MatchRegex(GrowthSignalLinePattern());
		growthSignalLines[0].Should().Contain("operation=HierarchyTotals");
		AssertGrowthSignalValues(logger, "HierarchyTotals");
	}

	[Fact]
	public async Task GetCostDetailsAsync_logs_a_growth_signal_matching_only_the_compact_field_template()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(new(1), LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var logger = new CapturingLogger<CostQueries>();
		var sut = new CostQueries(port, logger);

		_ = await sut.GetCostDetailsAsync(new() { Context = ContextFor(CostViewerId), NodeId = LeafId, AsOf = At(24) });

		var growthSignalLines = logger.Messages.Where(message => message.StartsWith("cost_read_growth_signal", StringComparison.Ordinal)).ToArray();
		growthSignalLines.Should().ContainSingle();
		growthSignalLines[0].Should().MatchRegex(GrowthSignalLinePattern());
		growthSignalLines[0].Should().Contain("operation=CostDetails");
		AssertGrowthSignalValues(logger, "CostDetails");
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_logs_a_growth_signal_matching_only_the_compact_field_template()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(new(1), LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var logger = new CapturingLogger<CostQueries>();
		var sut = new CostQueries(port, logger);

		_ = await sut.GetBulkNodeCostsAsync(new() { Context = ContextFor(CostViewerId), NodeIds = [BranchId], AsOf = At(24) });

		var growthSignalLines = logger.Messages.Where(message => message.StartsWith("cost_read_growth_signal", StringComparison.Ordinal)).ToArray();
		growthSignalLines.Should().ContainSingle();
		growthSignalLines[0].Should().MatchRegex(GrowthSignalLinePattern());
		growthSignalLines[0].Should().Contain("operation=BulkNodeCosts");
		AssertGrowthSignalValues(logger, "BulkNodeCosts");
	}

	[Fact]
	public async Task GetRequesterVisibleHierarchyAsync_logs_a_growth_signal_matching_only_the_compact_field_template()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(new(1), LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var logger = new CapturingLogger<CostQueries>();
		var sut = new CostQueries(port, logger);

		_ = await sut.GetRequesterVisibleHierarchyAsync(BranchId, At(24));

		var growthSignalLines = logger.Messages.Where(message => message.StartsWith("cost_read_growth_signal", StringComparison.Ordinal)).ToArray();
		growthSignalLines.Should().ContainSingle();
		growthSignalLines[0].Should().MatchRegex(GrowthSignalLinePattern());
		growthSignalLines[0].Should().Contain("operation=RequesterVisibleHierarchy");
		AssertGrowthSignalValues(logger, "RequesterVisibleHierarchy");
	}
}
