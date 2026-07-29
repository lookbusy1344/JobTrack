namespace JobTrack.Application;

using Abstractions;
using Domain.Costing;
using NodaTime;
using Ports;

/// <summary>
///     Implements requester intake commands (ADR 0033) by delegating to
///     <see cref="IJobRequestCommandPort" />, which owns authorization and the transaction — the same
///     shape as <see cref="JobCommands" />.
/// </summary>
internal sealed class RequestCommands : IRequestCommands
{
	private readonly IJobRequestCommandPort _port;
	private readonly IRequesterDurationQueries _durationQueries;
	private readonly IClock _clock;

	/// <summary>Creates a <see cref="RequestCommands" /> over the given port.</summary>
	public RequestCommands(IJobRequestCommandPort port, IRequesterDurationQueries durationQueries, IClock clock)
	{
		ArgumentNullException.ThrowIfNull(port);
		ArgumentNullException.ThrowIfNull(durationQueries);
		ArgumentNullException.ThrowIfNull(clock);

		_port = port;
		_durationQueries = durationQueries;
		_clock = clock;
	}

	/// <inheritdoc />
	public Task<JobRequestResult> SubmitAsync(SubmitJobRequestRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"requests.submit", request.Context, null,
			() => _port.SubmitAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobNodeResult> MoveAsync(MoveRequesterJobRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"requests.move", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.MoveAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<EquatableArray<JobRequestSummaryResult>> GetMyRequestsAsync(
		CommandContext context, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		return JobTrackOperation.TraceAsync(
			"requests.get-mine", context, null,
			() => _port.GetMyRequestsAsync(context, cancellationToken));
	}

	/// <inheritdoc />
	public Task<EquatableArray<HoldingAreaSummaryResult>> GetEligibleHoldingAreasAsync(
		CommandContext context, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		return JobTrackOperation.TraceAsync(
			"requests.get-eligible-holding-areas", context, null,
			() => _port.GetEligibleHoldingAreasAsync(context, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobRequestResult> AcknowledgeAsync(AcknowledgeJobRequestRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"requests.acknowledge", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.AcknowledgeAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobRequestNoteResult> AddNoteAsync(AddJobRequestNoteRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"requests.add-note", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.AddNoteAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobRequestDetailResult> GetDetailAsync(GetJobRequestDetailRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"requests.get-detail", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			async () => {
				// The request port performs the authoritative per-request authorization before this
				// internal duration projection reads work-derived facts.
				var detail = await _port.GetDetailAsync(request, cancellationToken).ConfigureAwait(false);
				var durations = await _durationQueries.GetRequesterVisibleHierarchyAsync(
						request.NodeId, _clock.GetCurrentInstant(), cancellationToken)
					.ConfigureAwait(false);

				return detail with {
					Subtree = EquatableArray.CopyOf(
						detail.Subtree.Select(node => node with {
							AllocatedDuration = durations.GetValueOrDefault(node.JobNodeId, AllocatedDuration.Zero),
						})),
				};
			});
	}
}
