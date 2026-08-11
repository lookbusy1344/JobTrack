namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Result of <see cref="IWorkCommands.ReopenAndStartWorkAsync" />.</summary>
public sealed record ReopenAndStartWorkResult
{
	/// <summary>The reopened leaf.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>Always <see cref="Achievement.InProgress" /> (ADR 0038's auto-advance applied inside the same commit).</summary>
	public required Achievement Achievement { get; init; }

	/// <summary>The instant the leaf's achievement was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The leaf's new optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>The newly started session.</summary>
	public required WorkSessionResult Session { get; init; }
}
