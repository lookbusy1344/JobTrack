namespace JobTrack.Web;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Domain.Hierarchy;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

internal static partial class JobTrackApi
{
	private static async Task<IResult> GetRootJobNodeAsync(
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = null }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetJobNodeAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = new JobNodeId(nodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetJobChildrenAsync(
		long nodeId,
		long? ownerUserId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		JobArchiveFilter archiveFilter = JobArchiveFilter.ActiveOnly,
		bool unassignedOnly = false,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var ownership = ResolveOwnership(ownerUserId, unassignedOnly);

			// Fresh-eyes review §2.8: cost enrichment happens inside GetJobChildrenAsync, so the page
			// itself is fetched at exactly pageSize -- never the pageSize + 1 probe row -- and "is there
			// another page" is answered by a second, unenriched-scale (Limit = 1) call, skipped entirely
			// when this page didn't even fill up.
			var page = await jobTrackClient.Query.GetJobChildrenAsync(new() {
				Context = context,
				ParentId = new(nodeId),
				Ownership = ownership,
				ArchiveFilter = archiveFilter,
				Offset = offset,
				Limit = resolvedPageSize,
			}, cancellationToken);

			var hasMore = false;
			if (page.Count == resolvedPageSize) {
				var probe = await jobTrackClient.Query.GetJobChildrenAsync(new() {
					Context = context,
					ParentId = new(nodeId),
					Ownership = ownership,
					ArchiveFilter = archiveFilter,
					Offset = offset + resolvedPageSize,
					Limit = 1,
				}, cancellationToken);
				hasMore = probe.Count > 0;
			}

			return TypedResults.Ok(new PagedResponse<JobNodeSummaryResponse> {
				Items = [.. page.Select(Map)],
				Offset = offset,
				PageSize = resolvedPageSize,
				HasMore = hasMore,
				OrderedBy = "id ascending",
			});
		});
	}

	private static async Task<IResult> SearchJobNodesAsync(
		[Required] string searchText,
		long? ownerUserId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		JobArchiveFilter archiveFilter = JobArchiveFilter.ActiveOnly,
		bool unassignedOnly = false,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var ownership = ResolveOwnership(ownerUserId, unassignedOnly);

			// Fresh-eyes review §2.8: same shape as GetJobChildrenAsync -- fetch exactly pageSize
			// enriched rows, only probe for another page (Limit = 1) when this page filled up.
			var page = await jobTrackClient.Query.SearchJobNodesAsync(new() {
				Context = context,
				SearchText = searchText,
				Ownership = ownership,
				ArchiveFilter = archiveFilter,
				Offset = offset,
				Limit = resolvedPageSize,
			}, cancellationToken);

			var hasMore = false;
			if (page.Count == resolvedPageSize) {
				var probe = await jobTrackClient.Query.SearchJobNodesAsync(new() {
					Context = context,
					SearchText = searchText,
					Ownership = ownership,
					ArchiveFilter = archiveFilter,
					Offset = offset + resolvedPageSize,
					Limit = 1,
				}, cancellationToken);
				hasMore = probe.Count > 0;
			}

			return TypedResults.Ok(new PagedResponse<JobNodeSummaryResponse> {
				Items = [.. page.Select(Map)],
				Offset = offset,
				PageSize = resolvedPageSize,
				HasMore = hasMore,
				OrderedBy = "id ascending",
			});
		});
	}

	/// <summary>
	///     <paramref name="unassignedOnly" /> and <paramref name="ownerUserId" /> are mutually exclusive
	///     filter shapes <see cref="OwnershipFilter" /> exists to keep distinct (ownership model §2.1) --
	///     a plain nullable owner id can't express both "no filter" and "only unassigned".
	///     <paramref name="unassignedOnly" /> wins if both are supplied.
	/// </summary>
	private static OwnershipFilter ResolveOwnership(long? ownerUserId, bool unassignedOnly) =>
		(unassignedOnly, ownerUserId) switch {
			(true, _) => OwnershipFilter.Unassigned,
			(false, long id) => OwnershipFilter.OwnedBy(new(id)),
			(false, null) => OwnershipFilter.All,
		};

	private static async Task<IResult> PickUpJobNodeAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Jobs.PickUpAsync(new() { Context = context, NodeId = new(nodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetReadinessAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetReadinessAsync(new() { Context = context, NodeId = new(nodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetPrerequisitesAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var result = await jobTrackClient.Query.GetPrerequisitesAsync(new() {
				Context = context,
				NodeId = new(nodeId),
				Offset = offset,
				Limit = resolvedPageSize + 1,
			}, cancellationToken);

			return TypedResults.Ok(ToPagedResponse(result, offset, resolvedPageSize, "requiredJobId ascending, dependentJobId ascending", Map));
		});
	}

	private static async Task<IResult> AddPrerequisiteAsync(
		long nodeId,
		[FromBody] AddPrerequisiteBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			await jobTrackClient.Jobs.AddPrerequisiteAsync(
				new() { Context = context, RequiredJobId = new(request.RequiredJobId), DependentJobId = new(nodeId) }, cancellationToken);

			return TypedResults.NoContent();
		});
	}

	private static async Task<IResult> RemovePrerequisiteAsync(
		long nodeId,
		long requiredJobId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			await jobTrackClient.Jobs.RemovePrerequisiteAsync(
				new() { Context = context, RequiredJobId = new(requiredJobId), DependentJobId = new(nodeId) }, cancellationToken);

			return TypedResults.NoContent();
		});
	}

	private static async Task<IResult> GetJobSubtreeAsync(
		long nodeId,
		int? depth,
		long? ownerUserId,
		DateTimeOffset? asOf,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		IClock clock,
		CancellationToken cancellationToken,
		JobArchiveFilter archiveFilter = JobArchiveFilter.ActiveOnly,
		bool unassignedOnly = false)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetJobSubtreeAsync(new() {
				Context = context,
				RootId = new(nodeId),
				MaxDepth = depth,
				Ownership = ResolveOwnership(ownerUserId, unassignedOnly),
				ArchiveFilter = archiveFilter,
				AsOf = asOf.HasValue ? Instant.FromDateTimeOffset(asOf.Value) : clock.GetCurrentInstant(),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static JobNodeDetailResponse Map(JobNodeDetailResult result) =>
		new() { Node = Map(result.Node), Ancestors = [.. result.Ancestors.Select(Map)] };

	private static JobNodeResponse Map(JobNodeResult result) =>
		new() {
			Id = result.Id.Value,
			ParentId = result.ParentId?.Value,
			Kind = result.Kind,
			HasChildren = result.HasChildren,
			HasLeafWork = result.HasLeafWork,
			Description = result.Description,
			WriteUp = result.WriteUp,
			PostedByUserId = result.PostedByUserId.Value,
			OwnerUserId = result.OwnerUserId?.Value,
			ExpectedDurationHours = result.ExpectedDurationHours,
			ExpectedCost = result.ExpectedCost?.Amount,
			NeededStart = result.NeededStart?.ToDateTimeOffset(),
			NeededFinish = result.NeededFinish?.ToDateTimeOffset(),
			Priority = result.Priority,
			PostedAt = result.PostedAt.ToDateTimeOffset(),
			ArchivedAt = result.ArchivedAt?.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static JobNodeAncestorResponse Map(JobNodeAncestorResult result) =>
		new() { Id = result.Id.Value, Description = result.Description, Kind = result.Kind };

	private static JobNodeSummaryResponse Map(JobNodeSummaryResult result) =>
		new() {
			Id = result.Id.Value,
			ParentId = result.ParentId?.Value,
			Kind = result.Kind,
			Description = result.Description,
			OwnerUserId = result.OwnerUserId?.Value,
			Priority = result.Priority,
			ArchivedAt = result.ArchivedAt?.ToDateTimeOffset(),
			HasChildren = result.HasChildren,
			HasLeafWork = result.HasLeafWork,
		};

	private static ReadinessResponse Map(ReadinessResult result) =>
		new() { IsReady = result.IsReady, Blockers = [.. result.Blockers.Select(Map)] };

	private static UnsatisfiedPrerequisiteResponse Map(UnsatisfiedPrerequisite result) =>
		new() { RequiredJobId = result.RequiredJobId.Value, DeclaredOnJobId = result.DeclaredOnJobId.Value };

	private static JobSubtreeResponse Map(JobSubtreeResult result) =>
		new() {
			RootId = result.RootId.Value,
			RootAchievement = result.RootAchievement,
			RootTotal = result.RootTotal?.Amount,
			TzdbVersion = result.TzdbVersion,
			Nodes = [.. result.Nodes.Select(Map)],
		};

	private static JobSubtreeNodeResponse Map(JobSubtreeNodeResult result) =>
		new() {
			Id = result.Id.Value,
			ParentId = result.ParentId?.Value,
			Kind = result.Kind,
			Depth = result.Depth,
			Description = result.Description,
			OwnerUserId = result.OwnerUserId?.Value,
			Priority = result.Priority,
			ArchivedAt = result.ArchivedAt?.ToDateTimeOffset(),
			HasChildren = result.HasChildren,
			HasLeafWork = result.HasLeafWork,
			IsReady = result.IsReady,
			HasUnexpandedChildren = result.HasUnexpandedChildren,
			MatchesFilter = result.MatchesFilter,
			SubtreeLft = result.SubtreeLft,
			SubtreeRgt = result.SubtreeRgt,
			Cost = result.Cost?.Amount,
		};

	private static PrerequisiteEdgeResponse Map(PrerequisiteEdge result) =>
		new() { RequiredJobId = result.RequiredJobId.Value, DependentJobId = result.DependentJobId.Value };

	internal sealed class JobNodeDetailResponse
	{
		public required JobNodeResponse Node { get; init; }

		public required JobNodeAncestorResponse[] Ancestors { get; init; }
	}

	internal sealed class JobNodeResponse
	{
		public required long Id { get; init; }

		public long? ParentId { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }

		/// <summary>Whether this node has at least one direct child.</summary>
		public required bool HasChildren { get; init; }

		/// <summary>Whether this node has an attached leaf-work row.</summary>
		public required bool HasLeafWork { get; init; }

		public required string Description { get; init; }

		public string? WriteUp { get; init; }

		public required long PostedByUserId { get; init; }

		public required long? OwnerUserId { get; init; }

		public decimal? ExpectedDurationHours { get; init; }

		public decimal? ExpectedCost { get; init; }

		public DateTimeOffset? NeededStart { get; init; }

		public DateTimeOffset? NeededFinish { get; init; }

		public required Priority Priority { get; init; }

		public required DateTimeOffset PostedAt { get; init; }

		public DateTimeOffset? ArchivedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class JobNodeAncestorResponse
	{
		public required long Id { get; init; }

		public required string Description { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }
	}

	internal sealed class JobNodeSummaryResponse
	{
		public required long Id { get; init; }

		public long? ParentId { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }

		public required string Description { get; init; }

		public required long? OwnerUserId { get; init; }

		public required Priority Priority { get; init; }

		public DateTimeOffset? ArchivedAt { get; init; }

		/// <summary>Whether this node has at least one direct child.</summary>
		public required bool HasChildren { get; init; }

		/// <summary>Whether this node has an attached leaf-work row.</summary>
		public required bool HasLeafWork { get; init; }
	}

	internal sealed class ReadinessResponse
	{
		public required bool IsReady { get; init; }

		public required UnsatisfiedPrerequisiteResponse[] Blockers { get; init; }
	}

	internal sealed class UnsatisfiedPrerequisiteResponse
	{
		public required long RequiredJobId { get; init; }

		public required long DeclaredOnJobId { get; init; }
	}

	internal sealed class PrerequisiteEdgeResponse
	{
		public required long RequiredJobId { get; init; }

		public required long DependentJobId { get; init; }
	}

	internal sealed class JobSubtreeResponse
	{
		public required long RootId { get; init; }

		/// <summary>
		///     The root's computed rollup when it is a branch or the permanent root; null for a leaf.
		/// </summary>
		public BranchAchievement? RootAchievement { get; init; }

		/// <summary>Null when the actor may not view this subtree's cost (ADR 0040) -- never a whole-request denial.</summary>
		public decimal? RootTotal { get; init; }

		/// <summary>Null exactly when <see cref="RootTotal" /> is.</summary>
		public string? TzdbVersion { get; init; }

		public required JobSubtreeNodeResponse[] Nodes { get; init; }
	}

	internal sealed class JobSubtreeNodeResponse
	{
		public required long Id { get; init; }

		public long? ParentId { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }

		/// <summary>Depth below the requested subtree root; the root itself is 0.</summary>
		public required int Depth { get; init; }

		public required string Description { get; init; }

		public long? OwnerUserId { get; init; }

		public required Priority Priority { get; init; }

		public DateTimeOffset? ArchivedAt { get; init; }

		/// <summary>Whether this node has at least one direct child.</summary>
		public required bool HasChildren { get; init; }

		/// <summary>Whether this node has an attached leaf-work row.</summary>
		public required bool HasLeafWork { get; init; }

		/// <summary>
		///     Whether every prerequisite declared on this node or on any ancestor is satisfied (spec §6,
		///     ADR 0043). Aggregates over ancestors, never over descendants: a branch stays ready when a
		///     descendant of it is blocked.
		/// </summary>
		public required bool IsReady { get; init; }

		/// <summary>Whether this node has children beyond what this fetch expanded (ADR 0039) -- drill in for the rest.</summary>
		public required bool HasUnexpandedChildren { get; init; }

		/// <summary>Whether this node itself matched the requested ownership/archive filter (ADR 0039 decision 5).</summary>
		public required bool MatchesFilter { get; init; }

		/// <summary>Ordinal pre-order position within this fetch, rebased to 0 at the subtree root (ADR 0039 decision 3).</summary>
		public required int SubtreeLft { get; init; }

		/// <summary>Ordinal post-order position paired with <see cref="SubtreeLft" />.</summary>
		public required int SubtreeRgt { get; init; }

		/// <summary>Null when the actor may not view this subtree's cost (ADR 0040) -- never a whole-request denial.</summary>
		public decimal? Cost { get; init; }
	}

	internal sealed class AddPrerequisiteBody
	{
		public required long RequiredJobId { get; init; }
	}
}
