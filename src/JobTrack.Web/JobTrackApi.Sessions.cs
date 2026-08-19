namespace JobTrack.Web;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

internal static partial class JobTrackApi
{
	private static async Task<IResult> GetLeafSessionsAsync(
		long nodeId,
		long workedByUserId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var result = await jobTrackClient.Query.GetLeafSessionsAsync(new() {
				Context = context,
				LeafWorkId = new(nodeId),
				WorkedByUserId = new AppUserId(workedByUserId),
				Offset = offset,
				Limit = resolvedPageSize + 1,
			}, cancellationToken);

			return TypedResults.Ok(ToPagedResponse(result, offset, resolvedPageSize, "startedAt descending, id descending", Map));
		});
	}

	private static async Task<IResult> StartSessionAsync(
		long nodeId,
		[FromBody] StartSessionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.StartSessionAsync(new() {
				Context = context,
				LeafWorkId = new(nodeId),
				WorkedByUserId = new(request.WorkedByUserId),
				StartedAt = request.StartedAt.HasValue ? Instant.FromDateTimeOffset(request.StartedAt.Value) : null,
			}, cancellationToken);

			return TypedResults.Created($"/api/jobs/{nodeId}/sessions/{result.Id.Value}", Map(result));
		});
	}

	private static async Task<IResult> FinishSessionAsync(
		long nodeId,
		long sessionId,
		[FromBody] FinishSessionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.FinishSessionAsync(new() {
				Context = context,
				SessionId = new(sessionId),
				Version = request.Version,
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				LeafWorkId = new JobNodeId(nodeId),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> FinishSessionAndUpdateWriteUpAsync(
		long nodeId,
		long sessionId,
		[FromBody] FinishSessionAndUpdateWriteUpBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var writeUpChange = request.WriteUpChange;
			var result = await jobTrackClient.Work.FinishSessionAndUpdateWriteUpAsync(new() {
				Context = context,
				SessionId = new(sessionId),
				Version = request.Version,
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				LeafWorkId = new JobNodeId(nodeId),
				WriteUpChange = writeUpChange is not null
					? new() {
						NodeVersion = writeUpChange.NodeVersion,
						WriteUp = writeUpChange.WriteUp,
					}
					: null,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CorrectSessionAsync(
		long nodeId,
		long sessionId,
		[FromBody] CorrectSessionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.CorrectSessionAsync(new() {
				Context = context,
				SessionId = new(sessionId),
				StartedAt = Instant.FromDateTimeOffset(request.StartedAt),
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				Reason = request.Reason,
				Version = request.Version,
				LeafWorkId = new JobNodeId(nodeId),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetLeafWorkAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetLeafWorkAsync(new() {
				Context = context,
				JobNodeId = new(nodeId),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> SetAchievementAsync(
		long nodeId,
		[FromBody] SetAchievementBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.SetAchievementAsync(new() {
				Context = context,
				JobNodeId = new(nodeId),
				NewAchievement = request.NewAchievement,
				Reason = request.Reason,
				Version = request.Version,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CompleteLeafAsync(
		long nodeId,
		[FromBody] CompleteLeafBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var writeUpChange = request.WriteUpChange;
			var result = await jobTrackClient.Work.CompleteLeafAsync(new() {
				Context = context,
				JobNodeId = new(nodeId),
				Version = request.Version,
				ExpectedActiveSessions = [
					.. request.ExpectedActiveSessions.Select(s => new ExpectedActiveSession {
						Id = new(s.Id), Version = s.Version,
					}),
				],
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				CompletionNote = request.CompletionNote,
				FinalAchievement = request.FinalAchievement ?? Achievement.Success,
				WriteUpChange = writeUpChange is not null
					? new() {
						NodeVersion = writeUpChange.NodeVersion,
						WriteUp = writeUpChange.WriteUp,
					}
					: null,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> ReopenAndStartWorkAsync(
		long nodeId,
		[FromBody] ReopenAndStartWorkBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.ReopenAndStartWorkAsync(new() {
				Context = context,
				JobNodeId = new(nodeId),
				Version = request.Version,
				Reason = request.Reason,
				WorkedByUserId = new(request.WorkedByUserId),
				StartedAt = request.StartedAt.HasValue ? Instant.FromDateTimeOffset(request.StartedAt.Value) : null,
			}, cancellationToken);

			return TypedResults.Created($"/api/jobs/{nodeId}/sessions/{result.Session.Id.Value}", Map(result));
		});
	}

	private static LeafWorkResponse Map(LeafWorkResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Achievement = result.Achievement,
			PartialCriteria = result.PartialCriteria,
			FullCriteria = result.FullCriteria,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static WorkSessionResponse Map(WorkSessionResult result) =>
		new() {
			Id = result.Id.Value,
			LeafWorkId = result.LeafWorkId.Value,
			WorkedByUserId = result.WorkedByUserId.Value,
			StartedAt = result.StartedAt.ToDateTimeOffset(),
			FinishedAt = result.FinishedAt?.ToDateTimeOffset(),
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static CompleteLeafResponse Map(CompleteLeafResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Achievement = result.Achievement,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
			FinishedSessions = [.. result.FinishedSessions.Select(Map)],
			WriteUpChanged = result.WriteUpChanged,
			Node = result.Node is not null ? Map(result.Node) : null,
		};

	private static FinishSessionAndUpdateWriteUpResponse Map(FinishSessionAndUpdateWriteUpResult result) =>
		new() {
			Session = Map(result.Session),
			WriteUpChanged = result.WriteUpChanged,
			Node = result.Node is not null ? Map(result.Node) : null,
		};

	private static ReopenAndStartWorkResponse Map(ReopenAndStartWorkResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Achievement = result.Achievement,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
			Session = Map(result.Session),
		};

	internal sealed class LeafWorkResponse
	{
		public required long JobNodeId { get; init; }

		public required Achievement Achievement { get; init; }

		public string? PartialCriteria { get; init; }

		public string? FullCriteria { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class SetAchievementBody
	{
		public required Achievement NewAchievement { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class WorkSessionResponse
	{
		public required long Id { get; init; }

		public required long LeafWorkId { get; init; }

		public required long WorkedByUserId { get; init; }

		public required DateTimeOffset StartedAt { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class StartSessionBody
	{
		public required long WorkedByUserId { get; init; }

		public DateTimeOffset? StartedAt { get; init; }
	}

	internal sealed class FinishSessionBody
	{
		public required long Version { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }
	}

	/// <summary>
	///     Nested write-up change (remediation plan §2.1) -- omitted entirely on the containing body
	///     means "no write-up change"; present with <see cref="WriteUp" /> itself <see langword="null" />
	///     means "clear the write-up".
	/// </summary>
	internal sealed class WriteUpChangeBody
	{
		public required long NodeVersion { get; init; }

		public string? WriteUp { get; init; }
	}

	internal sealed class FinishSessionAndUpdateWriteUpBody
	{
		public required long Version { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		public WriteUpChangeBody? WriteUpChange { get; init; }
	}

	internal sealed class FinishSessionAndUpdateWriteUpResponse
	{
		public required WorkSessionResponse Session { get; init; }

		public required bool WriteUpChanged { get; init; }

		public JobNodeResponse? Node { get; init; }
	}

	internal sealed class CorrectSessionBody
	{
		public required DateTimeOffset StartedAt { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class ExpectedActiveSessionBody
	{
		public required long Id { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class CompleteLeafBody
	{
		public required long Version { get; init; }

		public required ExpectedActiveSessionBody[] ExpectedActiveSessions { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		public string? CompletionNote { get; init; }

		/// <summary>
		///     The achievement to record (ADR 0047) -- <see langword="null" /> (the wire default) means
		///     <see cref="Achievement.Success" />, preserving every existing client's behavior.
		/// </summary>
		public Achievement? FinalAchievement { get; init; }

		/// <summary>An optional write-up change applied in the same commit as this completion (remediation plan §2.1).</summary>
		public WriteUpChangeBody? WriteUpChange { get; init; }
	}

	internal sealed class CompleteLeafResponse
	{
		public required long JobNodeId { get; init; }

		public required Achievement Achievement { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }

		public required WorkSessionResponse[] FinishedSessions { get; init; }

		public required bool WriteUpChanged { get; init; }

		public JobNodeResponse? Node { get; init; }
	}

	internal sealed class ReopenAndStartWorkBody
	{
		public required long Version { get; init; }

		[Required] public required string Reason { get; init; }

		public required long WorkedByUserId { get; init; }

		public DateTimeOffset? StartedAt { get; init; }
	}

	internal sealed class ReopenAndStartWorkResponse
	{
		public required long JobNodeId { get; init; }

		public required Achievement Achievement { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }

		public required WorkSessionResponse Session { get; init; }
	}

	private static void MapSessionEndpoints(this RouteGroupBuilder api)
	{
		_ = api.MapGet("/jobs/{nodeId:long}/sessions", GetLeafSessionsAsync)
			   .RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			   .WithName("GetLeafSessions")
			   .WithSummary("Get one worker's sessions on a leaf, most recent first, paged (offset/pageSize).")
			   .Produces<PagedResponse<WorkSessionResponse>>()
			   .ProducesProblem(StatusCodes.Status400BadRequest)
			   .ProducesProblem(StatusCodes.Status401Unauthorized)
			   .ProducesProblem(StatusCodes.Status403Forbidden)
			   .ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPost("/jobs/{nodeId:long}/sessions", StartSessionAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.JobWorkflow,
				   "StartSession",
				   "Start a new work session on a leaf. Calling this again for an already-active worker/leaf pair is how a UI \"resume\" action is expressed.")
			   .Produces<WorkSessionResponse>(StatusCodes.Status201Created);

		_ = api.MapPost("/jobs/{nodeId:long}/sessions/{sessionId:long}/finish", FinishSessionAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.JobWorkflow,
				   "FinishSession",
				   "Finish the active session. \"Pause\" and \"stop\" are UI descriptions of this same operation.")
			   .Produces<WorkSessionResponse>();

		_ = api.MapPost("/jobs/{nodeId:long}/sessions/{sessionId:long}/finish-and-update-write-up", FinishSessionAndUpdateWriteUpAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.JobWorkflow,
				   "FinishSessionAndUpdateWriteUp",
				   "Atomic composite (remediation plan §2.1): finish the active session and, optionally, apply a write-up change to its leaf's node, in one commit. The plain finish endpoint above remains for a caller with no write-up to change.")
			   .Produces<FinishSessionAndUpdateWriteUpResponse>();

		_ = api.MapPost("/jobs/{nodeId:long}/sessions/{sessionId:long}/correct", CorrectSessionAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.JobWorkflow,
				   "CorrectSession",
				   "Correct a historical session's start and/or finish instants, with an audited reason.")
			   .Produces<WorkSessionResponse>();

		_ = api.MapGet("/jobs/{nodeId:long}/achievement", GetLeafWorkAsync)
			   .RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			   .WithName("GetLeafWork")
			   .WithSummary("Get a leaf's current achievement state.")
			   .Produces<LeafWorkResponse>()
			   .ProducesProblem(StatusCodes.Status401Unauthorized)
			   .ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPut("/jobs/{nodeId:long}/achievement", SetAchievementAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.JobWorkflow, "SetAchievement", "Transition a leaf's achievement state, with an audited reason.")
			   .Produces<LeafWorkResponse>();

		_ = api.MapPost("/jobs/{nodeId:long}/complete", CompleteLeafAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.JobWorkflow,
				   "CompleteLeaf",
				   "Atomically finish the exact confirmed active-session set and record an achievement -- Success by default, or Cancelled/Unsuccessful (ADR 0045/0047). Composite of finish-session(s) and set-achievement.")
			   .Produces<CompleteLeafResponse>();

		_ = api.MapPost("/jobs/{nodeId:long}/reopen-and-start-session", ReopenAndStartWorkAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.JobWorkflow,
				   "ReopenAndStartWork",
				   "Atomically reopen a terminal leaf to Waiting, auto-advance to InProgress (ADR 0038), and start the target worker's session (ADR 0045).")
			   .Produces<ReopenAndStartWorkResponse>(StatusCodes.Status201Created);
	}
}
