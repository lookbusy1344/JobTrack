namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One newly identified child job created by <see cref="IJobCommands.DecomposeWorkedLeafAsync" />.
///     Shares <see cref="CreateJobNodeRequest" />'s field set minus <c>Context</c>/<c>ParentId</c>,
///     which are shared across the whole decomposition rather than per child.
/// </summary>
public sealed record NewChildJobSpec
{
	/// <summary>The new child's description.</summary>
	public required string Description { get; init; }

	/// <summary>Free-form supplementary detail.</summary>
	public string? WriteUp { get; init; }

	/// <summary>
	///     The employee who directly owns the new child and, for authorization, its subtree; <see langword="null" /> to leave it unassigned (the pickup
	///     pool).
	/// </summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The new child's priority.</summary>
	public required Priority Priority { get; init; }

	/// <summary>The estimated effort, in hours.</summary>
	public decimal? ExpectedDurationHours { get; init; }

	/// <summary>The estimated cost.</summary>
	public Money? ExpectedCost { get; init; }

	/// <summary>The instant this child's work is needed to start.</summary>
	public Instant? NeededStart { get; init; }

	/// <summary>The instant this child's work is needed to finish.</summary>
	public Instant? NeededFinish { get; init; }
}
