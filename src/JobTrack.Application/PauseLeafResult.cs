namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IWorkCommands.PauseLeafAsync" />.</summary>
public sealed record PauseLeafResult
{
	/// <summary>The paused leaf.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>
	///     Every session finished by this pause, at the same captured instant -- the exact set confirmed
	///     by <see cref="PauseLeafRequest.ExpectedActiveSessions" />, possibly empty.
	/// </summary>
	public required EquatableArray<WorkSessionResult> FinishedSessions { get; init; }

	/// <summary>
	///     Whether <see cref="PauseLeafRequest.WriteUpChange" /> actually changed the stored write-up
	///     text -- always <see langword="false" /> when no write-up change was requested, or when the
	///     submitted text already matched what was stored.
	/// </summary>
	public required bool WriteUpChanged { get; init; }

	/// <summary>
	///     The leaf's node after this pause, when <see cref="PauseLeafRequest.WriteUpChange" /> was
	///     supplied; otherwise <see langword="null" />.
	/// </summary>
	public JobNodeResult? Node { get; init; }
}
