namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IWorkCommands.SetAchievementAsync" /> (plan §7.3 step 7; ADR 0001). Every
///     transition — not only reopening — records <see cref="Reason" /> in the resulting audit event
///     (ADR 0001 consequences).
/// </summary>
public sealed record SetAchievementRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf whose <c>LeafWork</c> achievement is changing.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The requested new achievement state.</summary>
	public required Achievement NewAchievement { get; init; }

	/// <summary>Why this transition is being made.</summary>
	public required string Reason { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
