namespace JobTrack.Web;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

internal static partial class JobTrackApi
{
	private static async Task<IResult> GetEligibleHoldingAreasAsync(
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.GetEligibleHoldingAreasAsync(context, cancellationToken);

			return TypedResults.Ok(result.Select(Map).ToArray());
		});
	}

	private static async Task<IResult> SubmitRequestAsync(
		[FromBody] SubmitRequestBody? request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		var validationProblem = ValidateSubmitRequestBody(request);
		if (validationProblem is not null) {
			return validationProblem;
		}

		var body = request ?? throw new ArgumentNullException(nameof(request));
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.SubmitAsync(
				new() { Context = context, HoldingAreaId = new(body.HoldingAreaId), Description = body.Description }, cancellationToken);

			return TypedResults.Created("/api/requests", Map(result));
		});
	}

	private static async Task<IResult> GetMyRequestsAsync(
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.GetMyRequestsAsync(context, cancellationToken);

			return TypedResults.Ok(result.Select(Map).ToArray());
		});
	}

	private static async Task<IResult> GetRequestDetailAsync(
		long jobNodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.GetDetailAsync(new() { Context = context, NodeId = new(jobNodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddRequestNoteAsync(
		long jobNodeId,
		[FromBody] AddRequestNoteBody? request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		var validationProblem = ValidateAddRequestNoteBody(request);
		if (validationProblem is not null) {
			return validationProblem;
		}

		var body = request ?? throw new ArgumentNullException(nameof(request));
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.AddNoteAsync(new() {
				Context = context,
				NodeId = new(jobNodeId),
				Content = body.Content,
				VisibleToRequester = body.VisibleToRequester,
			}, cancellationToken);

			return TypedResults.Created($"/api/requests/{jobNodeId}", Map(result));
		});
	}

	private static ProblemHttpResult? ValidateSubmitRequestBody(SubmitRequestBody? request)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Description)) {
			return Problem(
				StatusCodes.Status400BadRequest,
				"Invalid request",
				"The request description is required.",
				ValidationProblemType);
		}

		return null;
	}

	private static ProblemHttpResult? ValidateAddRequestNoteBody(AddRequestNoteBody? request)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Content)) {
			return Problem(
				StatusCodes.Status400BadRequest,
				"Invalid request",
				"The note content is required.",
				ValidationProblemType);
		}

		return null;
	}

	private static async Task<IResult> AcknowledgeRequestAsync(
		long jobNodeId,
		[FromBody] AcknowledgeRequestBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.AcknowledgeAsync(
				new() { Context = context, NodeId = new(jobNodeId), Version = request.Version }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static HoldingAreaResponse Map(HoldingAreaSummaryResult result) =>
		new() { Id = result.Id.Value, Name = result.Name };

	private static RequestResponse Map(JobRequestResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			SubmittedAt = result.SubmittedAt.ToDateTimeOffset(),
			AcknowledgedAt = result.AcknowledgedAt?.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static RequestResponse Map(JobRequestSummaryResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			SubmittedAt = result.SubmittedAt.ToDateTimeOffset(),
			AcknowledgedAt = null,
			Version = result.Version,
		};

	private static RequestDetailResponse Map(JobRequestDetailResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			Status = result.Status,
			SubmittedAt = result.SubmittedAt.ToDateTimeOffset(),
			AcknowledgedAt = result.AcknowledgedAt?.ToDateTimeOffset(),
			Version = result.Version,
			Subtree = [.. result.Subtree.Select(Map)],
			Notes = [.. result.Notes.Select(Map)],
		};

	private static RequesterSubtreeNodeResponse Map(RequesterSubtreeNodeResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			Status = result.Status,
			ParentId = result.ParentId?.Value,
			LastUpdatedAt = result.LastUpdatedAt.ToDateTimeOffset(),
		};

	private static RequestNoteResponse Map(JobRequestNoteResult result) =>
		new() {
			Id = result.Id.Value,
			AuthorUserId = result.AuthorUserId.Value,
			Content = result.Content,
			VisibleToRequester = result.VisibleToRequester,
			CreatedAt = result.CreatedAt.ToDateTimeOffset(),
		};

	internal sealed class HoldingAreaResponse
	{
		public required long Id { get; init; }

		public required string Name { get; init; }
	}

	internal sealed class RequestResponse
	{
		public required long JobNodeId { get; init; }

		public required string Description { get; init; }

		public required DateTimeOffset SubmittedAt { get; init; }

		public DateTimeOffset? AcknowledgedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class SubmitRequestBody
	{
		[Required] public required string Description { get; init; }

		public required long HoldingAreaId { get; init; }
	}

	internal sealed class RequestDetailResponse
	{
		public required long JobNodeId { get; init; }

		public required string Description { get; init; }

		public required RequesterStatus Status { get; init; }

		public required DateTimeOffset SubmittedAt { get; init; }

		public DateTimeOffset? AcknowledgedAt { get; init; }

		public required long Version { get; init; }

		public required RequesterSubtreeNodeResponse[] Subtree { get; init; }

		public required RequestNoteResponse[] Notes { get; init; }
	}

	internal sealed class RequesterSubtreeNodeResponse
	{
		public required long JobNodeId { get; init; }

		public required string Description { get; init; }

		public required RequesterStatus Status { get; init; }

		public long? ParentId { get; init; }

		public required DateTimeOffset LastUpdatedAt { get; init; }
	}

	internal sealed class RequestNoteResponse
	{
		public required long Id { get; init; }

		public required long AuthorUserId { get; init; }

		public required string Content { get; init; }

		public required bool VisibleToRequester { get; init; }

		public required DateTimeOffset CreatedAt { get; init; }
	}

	internal sealed class AddRequestNoteBody
	{
		[Required] public required string Content { get; init; }

		public bool VisibleToRequester { get; init; }
	}

	internal sealed class AcknowledgeRequestBody
	{
		public required long Version { get; init; }
	}
}
