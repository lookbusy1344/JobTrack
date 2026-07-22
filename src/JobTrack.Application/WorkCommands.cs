namespace JobTrack.Application;

using Ports;

/// <summary>
///     Implements work-session and achievement commands (plan §7.3 steps 6–7) by delegating each
///     atomic operation to <see cref="IWorkSessionCommandPort" />/<see cref="IAchievementCommandPort" />,
///     which own authorization, prerequisite rechecking, and the transaction.
/// </summary>
public sealed class WorkCommands : IWorkCommands
{
	private readonly IAchievementCommandPort _achievementPort;
	private readonly IWorkSessionCommandPort _sessionPort;

	/// <summary>Creates a <see cref="WorkCommands" /> over the given ports.</summary>
	public WorkCommands(IWorkSessionCommandPort sessionPort, IAchievementCommandPort achievementPort)
	{
		ArgumentNullException.ThrowIfNull(sessionPort);
		ArgumentNullException.ThrowIfNull(achievementPort);

		_sessionPort = sessionPort;
		_achievementPort = achievementPort;
	}

	/// <inheritdoc />
	public Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"work.start-session", request.Context, JobTrackOperation.WithNodeId(request.LeafWorkId),
			() => _sessionPort.StartSessionAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"work.start-work", request.Context, JobTrackOperation.WithNodeId(request.JobNodeId),
			() => _sessionPort.StartWorkAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"work.finish-session", request.Context, null,
			() => _sessionPort.FinishSessionAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"work.correct-session", request.Context, null,
			() => _sessionPort.CorrectSessionAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ApplicationEnumValidation.ThrowIfInvalid(request.NewAchievement, nameof(request));

		return JobTrackOperation.TraceAsync(
			"work.set-achievement", request.Context, JobTrackOperation.WithNodeId(request.JobNodeId),
			() => _achievementPort.SetAchievementAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"work.complete-leaf", request.Context, JobTrackOperation.WithNodeId(request.JobNodeId),
			() => _sessionPort.CompleteLeafAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(
		ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason, nameof(request.Reason));

		return JobTrackOperation.TraceAsync(
			"work.reopen-and-start-work", request.Context, JobTrackOperation.WithNodeId(request.JobNodeId),
			() => _sessionPort.ReopenAndStartWorkAsync(request, cancellationToken));
	}
}
