namespace JobTrack.Application;

using Ports;

/// <summary>
///     Implements schedule commands (plan §7.3 step 8) by delegating each atomic operation to
///     <see cref="IScheduleCommandPort" />, which owns authorization and the transaction.
/// </summary>
internal sealed class ScheduleCommands : IScheduleCommands
{
	private readonly IScheduleCommandPort _port;

	/// <summary>Creates a <see cref="ScheduleCommands" /> over the given port.</summary>
	public ScheduleCommands(IScheduleCommandPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
	}

	/// <inheritdoc />
	public Task<ScheduleVersionResult> AddScheduleVersionAsync(
		AddScheduleVersionRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"schedules.add-version", request.Context, JobTrackOperation.WithUserId(request.UserId),
			() => _port.AddScheduleVersionAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<ScheduleExceptionResult> AddScheduleExceptionAsync(
		AddScheduleExceptionRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"schedules.add-exception", request.Context, JobTrackOperation.WithUserId(request.UserId),
			() => _port.AddScheduleExceptionAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<ScheduleVersionResult> CorrectScheduleVersionAsync(
		CorrectScheduleVersionRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"schedules.correct-version", request.Context, null,
			() => _port.CorrectScheduleVersionAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<ScheduleExceptionResult> CorrectScheduleExceptionAsync(
		CorrectScheduleExceptionRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"schedules.correct-exception", request.Context, null,
			() => _port.CorrectScheduleExceptionAsync(request, cancellationToken));
	}
}
