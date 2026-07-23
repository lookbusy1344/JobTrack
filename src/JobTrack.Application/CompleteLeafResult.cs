namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Result of <see cref="IWorkCommands.CompleteLeafAsync" />.</summary>
public sealed record CompleteLeafResult
{
	/// <summary>The completed leaf.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>Always <see cref="Achievement.Success" />.</summary>
	public required Achievement Achievement { get; init; }

	/// <summary>The instant the leaf's achievement was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The leaf's new optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     Every session finished by this completion, at the same captured instant -- the exact set
	///     confirmed by <see cref="CompleteLeafRequest.ExpectedActiveSessions" />, possibly empty.
	/// </summary>
	public required EquatableArray<WorkSessionResult> FinishedSessions { get; init; }

	/// <summary>
	///     Whether <see cref="CompleteLeafRequest.WriteUpChange" /> actually changed the stored write-up
	///     text -- always <see langword="false" /> when no write-up change was requested, or when the
	///     submitted text already matched what was stored.
	/// </summary>
	public required bool WriteUpChanged { get; init; }

	/// <summary>The leaf's node after this completion, when <see cref="CompleteLeafRequest.WriteUpChange" /> was supplied; otherwise <see langword="null" />.</summary>
	public JobNodeResult? Node { get; init; }
}
