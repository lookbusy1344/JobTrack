namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>One additional historical session reconstructed during a subtree import.</summary>
public sealed record ImportSubtreeWorkSessionSpec
{
	/// <summary>The employee who performed the session's work.</summary>
	public required AppUserId WorkedByUserId { get; init; }

	/// <summary>The instant the session started.</summary>
	public required Instant StartedAt { get; init; }

	/// <summary>The instant the session finished, or <see langword="null" /> while still active.</summary>
	public Instant? FinishedAt { get; init; }
}
