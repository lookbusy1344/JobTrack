namespace JobTrack.Application;

using Ports;

/// <summary>
///     Implements rate commands (plan §7.3 step 9) by delegating each atomic operation to
///     <see cref="IRateCommandPort" />, which owns authorization and the transaction.
/// </summary>
public sealed class RateCommands : IRateCommands
{
	private readonly IRateCommandPort _port;

	/// <summary>Creates a <see cref="RateCommands" /> over the given port.</summary>
	public RateCommands(IRateCommandPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
	}

	/// <inheritdoc />
	public Task<UserCostRateResult> AddUserCostRateAsync(
		AddUserCostRateRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"rates.add-user-rate", request.Context, JobTrackOperation.WithUserId(request.UserId),
			() => _port.AddUserCostRateAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<NodeRateOverrideResult> AddNodeRateOverrideAsync(
		AddNodeRateOverrideRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"rates.add-node-override", request.Context, JobTrackOperation.WithUserId(request.UserId),
			() => _port.AddNodeRateOverrideAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<UserCostRateResult> CorrectUserCostRateAsync(
		CorrectUserCostRateRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"rates.correct-user-rate", request.Context, null,
			() => _port.CorrectUserCostRateAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<NodeRateOverrideResult> CorrectNodeRateOverrideAsync(
		CorrectNodeRateOverrideRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"rates.correct-node-override", request.Context, null,
			() => _port.CorrectNodeRateOverrideAsync(request, cancellationToken));
	}
}
