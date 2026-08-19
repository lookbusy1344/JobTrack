namespace JobTrack.Web;

using Application;
using Domain.Costing;
using Domain.Rates;
using Identity;
using Microsoft.AspNetCore.Identity;
using NodaTime;

internal static partial class JobTrackApi
{
	private static async Task<IResult> GetCostDetailsAsync(
		long nodeId,
		DateTimeOffset? asOf,
		int? maxTraceSegments,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		IClock clock,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Costs.GetCostDetailsAsync(new() {
				Context = context,
				NodeId = new(nodeId),
				AsOf = asOf.HasValue ? Instant.FromDateTimeOffset(asOf.Value) : clock.GetCurrentInstant(),
				MaxTraceSegments = maxTraceSegments,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetHierarchyTotalsAsync(
		long nodeId,
		DateTimeOffset? asOf,
		int? maxHierarchyNodes,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		IClock clock,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Costs.GetHierarchyTotalsAsync(new() {
				Context = context,
				NodeId = new(nodeId),
				AsOf = asOf.HasValue ? Instant.FromDateTimeOffset(asOf.Value) : clock.GetCurrentInstant(),
				MaxHierarchyNodes = maxHierarchyNodes,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static CostDetailsResponse Map(CostDetailsResult result) =>
		new() {
			NodeId = result.NodeId.Value,
			ExactCost = result.ExactCost.Amount,
			DisplayedCost = result.DisplayedCost.Amount,
			AllocatedHours = result.AllocatedDuration.ToHours(),
			Trace = [.. result.Trace.Select(Map)],
			TzdbVersion = result.TzdbVersion,
		};

	private static CostSegmentTraceResponse Map(CostSegmentTrace trace) =>
		new() {
			SegmentStart = trace.Segment.Start.ToDateTimeOffset(),
			SegmentEnd = trace.Segment.End.ToDateTimeOffset(),
			IsWorkingTime = trace.IsWorkingTime,
			ActiveSessionIds = [.. trace.ActiveSessionIds.Select(id => id.Value)],
			SessionId = trace.SessionId.Value,
			NodeId = trace.NodeId.Value,
			SegmentTicks = trace.AllocatedDuration.SegmentTicks,
			ConcurrencyDivisor = trace.AllocatedDuration.ConcurrencyDivisor,
			AmountPerHour = trace.ResolvedRate.Rate.AmountPerHour,
			RateSource = trace.ResolvedRate.Source,
			UnroundedContribution = trace.UnroundedContribution.Amount,
		};

	private static HierarchyTotalsResponse Map(HierarchyTotalsResult result) =>
		new() {
			NodeId = result.NodeId.Value,
			Nodes = [
				.. result.ExactCosts.Select(entry => new HierarchyNodeCostResponse {
					NodeId = entry.Key.Value, ExactCost = entry.Value.Amount, DisplayedCost = result.DisplayedCosts[entry.Key].Amount, AllocatedHours = result.AllocatedDurations[entry.Key].ToHours(),
				}),
			],
			TzdbVersion = result.TzdbVersion,
		};

	private static void MapCostEndpoints(this RouteGroupBuilder api)
	{
		_ = api.MapGet("/jobs/{nodeId:long}/cost", GetCostDetailsAsync)
			   .RequireAuthorization(JobTrackPolicyNames.RateRead)
			   .WithName("GetCostDetails")
			   .WithSummary("Get one node's exact and displayed cost, with its rate-provenance segment trace (bounded; see plan §3.1).")
			   .Produces<CostDetailsResponse>()
			   .ProducesProblem(StatusCodes.Status400BadRequest)
			   .ProducesProblem(StatusCodes.Status401Unauthorized)
			   .ProducesProblem(StatusCodes.Status403Forbidden)
			   .ProducesProblem(StatusCodes.Status404NotFound)
			   .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

		_ = api.MapGet("/jobs/{nodeId:long}/cost/hierarchy", GetHierarchyTotalsAsync)
			   .RequireAuthorization(JobTrackPolicyNames.RateRead)
			   .WithName("GetHierarchyTotals")
			   .WithSummary("Get reconciled cost totals for a node and its entire subtree (bounded; see plan §3.1).")
			   .Produces<HierarchyTotalsResponse>()
			   .ProducesProblem(StatusCodes.Status400BadRequest)
			   .ProducesProblem(StatusCodes.Status401Unauthorized)
			   .ProducesProblem(StatusCodes.Status403Forbidden)
			   .ProducesProblem(StatusCodes.Status404NotFound)
			   .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
	}

	internal sealed class CostDetailsResponse
	{
		public required long NodeId { get; init; }

		public required decimal ExactCost { get; init; }

		public required decimal DisplayedCost { get; init; }

		public required decimal AllocatedHours { get; init; }

		public required CostSegmentTraceResponse[] Trace { get; init; }

		public required string TzdbVersion { get; init; }
	}

	internal sealed class CostSegmentTraceResponse
	{
		public required DateTimeOffset SegmentStart { get; init; }

		public required DateTimeOffset SegmentEnd { get; init; }

		public required bool IsWorkingTime { get; init; }

		public required long[] ActiveSessionIds { get; init; }

		public required long SessionId { get; init; }

		public required long NodeId { get; init; }

		public required long SegmentTicks { get; init; }

		public required int ConcurrencyDivisor { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required RateSource RateSource { get; init; }

		public required decimal UnroundedContribution { get; init; }
	}

	internal sealed class HierarchyTotalsResponse
	{
		public required long NodeId { get; init; }

		public required HierarchyNodeCostResponse[] Nodes { get; init; }

		public required string TzdbVersion { get; init; }
	}

	internal sealed class HierarchyNodeCostResponse
	{
		public required long NodeId { get; init; }

		public required decimal ExactCost { get; init; }

		public required decimal DisplayedCost { get; init; }

		public required decimal AllocatedHours { get; init; }
	}
}
