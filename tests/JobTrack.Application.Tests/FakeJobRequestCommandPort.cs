namespace JobTrack.Application.Tests;

using Abstractions;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IJobRequestCommandPort" /> for application-slice tests.
/// </summary>
internal sealed class FakeJobRequestCommandPort : IJobRequestCommandPort
{
	public SubmitJobRequestRequest? LastSubmitRequest { get; private set; }

	public MoveRequesterJobRequest? LastMoveRequest { get; private set; }

	public Task<JobRequestResult> SubmitAsync(SubmitJobRequestRequest request, CancellationToken cancellationToken = default)
	{
		LastSubmitRequest = request;
		return Task.FromResult(new JobRequestResult {
			JobNodeId = new(100),
			HoldingAreaId = request.HoldingAreaId,
			RequesterUserId = request.Context.Actor,
			OwnerUserId = null,
			Description = request.Description,
			SubmittedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			AcknowledgedAt = null,
			Version = 1,
		});
	}

	public Task<JobNodeResult> MoveAsync(MoveRequesterJobRequest request, CancellationToken cancellationToken = default)
	{
		LastMoveRequest = request;
		return Task.FromResult(new JobNodeResult {
			Id = request.NodeId,
			ParentId = request.NewParentId,
			Kind = NodeKind.Leaf,
			Description = "Requester job",
			PostedByUserId = request.Context.Actor,
			OwnerUserId = null,
			Priority = Priority.Medium,
			PostedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ArchivedAt = null,
			HasChildren = false,
			HasLeafWork = false,
			Version = request.Version + 1,
		});
	}

	public Task<EquatableArray<JobRequestSummaryResult>> GetMyRequestsAsync(
		CommandContext context, CancellationToken cancellationToken = default) =>
		Task.FromResult<EquatableArray<JobRequestSummaryResult>>([]);

	public Task<EquatableArray<HoldingAreaSummaryResult>> GetEligibleHoldingAreasAsync(
		CommandContext context, CancellationToken cancellationToken = default) =>
		Task.FromResult<EquatableArray<HoldingAreaSummaryResult>>([]);

	public Task<JobRequestResult> AcknowledgeAsync(AcknowledgeJobRequestRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<JobRequestNoteResult> AddNoteAsync(AddJobRequestNoteRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<JobRequestDetailResult> GetDetailAsync(GetJobRequestDetailRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();
}
