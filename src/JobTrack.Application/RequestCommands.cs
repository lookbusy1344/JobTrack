namespace JobTrack.Application;

using Abstractions;
using Ports;

/// <summary>
///     Implements requester intake commands (ADR 0033) by delegating to
///     <see cref="IJobRequestCommandPort" />, which owns authorization and the transaction — the same
///     shape as <see cref="JobCommands" />.
/// </summary>
public sealed class RequestCommands : IRequestCommands
{
	private readonly IJobRequestCommandPort _port;

	/// <summary>Creates a <see cref="RequestCommands" /> over the given port.</summary>
	public RequestCommands(IJobRequestCommandPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
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
			() => _port.GetDetailAsync(request, cancellationToken));
	}
}
