namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.AttachLeafWorkAsync" />. Attaches achievement tracking to an
///     an existing childless node created by <see cref="IJobCommands.AddChildAsync" />. Carries no
///     <c>Achievement</c> — every new <c>LeafWork</c> starts at <see cref="Achievement.Waiting" />
///     (database default); changing it is <see cref="IJobCommands" />'s step 7 command, not this one.
/// </summary>
public sealed record AttachLeafWorkRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf node to attach <c>LeafWork</c> to.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The criteria for partial achievement, if any.</summary>
	public string? PartialCriteria { get; init; }

	/// <summary>The criteria for full achievement, if any.</summary>
	public string? FullCriteria { get; init; }
}
