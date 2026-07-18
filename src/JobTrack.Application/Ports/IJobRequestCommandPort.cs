namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IRequestCommands" /> (ADR 0033). Each method is one
///     atomic transaction: the implementation reloads the actor's current roles and holding-area
///     eligibility and applies <see cref="Domain.Authorization.RequesterAccessPolicy" /> itself before
///     writing, the same shape as <see cref="IJobNodeCommandPort" />.
/// </summary>
public interface IJobRequestCommandPort
{
	/// <inheritdoc cref="IRequestCommands.SubmitAsync" />
	Task<JobRequestResult> SubmitAsync(SubmitJobRequestRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRequestCommands.MoveAsync" />
	Task<JobNodeResult> MoveAsync(MoveRequesterJobRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRequestCommands.GetMyRequestsAsync" />
	Task<EquatableArray<JobRequestSummaryResult>> GetMyRequestsAsync(CommandContext context, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRequestCommands.GetEligibleHoldingAreasAsync" />
	Task<EquatableArray<HoldingAreaSummaryResult>> GetEligibleHoldingAreasAsync(
		CommandContext context, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRequestCommands.AcknowledgeAsync" />
	Task<JobRequestResult> AcknowledgeAsync(AcknowledgeJobRequestRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRequestCommands.AddNoteAsync" />
	Task<JobRequestNoteResult> AddNoteAsync(AddJobRequestNoteRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRequestCommands.GetDetailAsync" />
	Task<JobRequestDetailResult> GetDetailAsync(GetJobRequestDetailRequest request, CancellationToken cancellationToken = default);
}
