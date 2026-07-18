namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Result of <see cref="IJobCommands.AttachLeafWorkAsync" />.</summary>
public sealed record LeafWorkResult
{
	/// <summary>The leaf node this <c>LeafWork</c> belongs to.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The current achievement state.</summary>
	public required Achievement Achievement { get; init; }

	/// <summary>The criteria for partial achievement, if any.</summary>
	public string? PartialCriteria { get; init; }

	/// <summary>The criteria for full achievement, if any.</summary>
	public string? FullCriteria { get; init; }

	/// <summary>The instant this <c>LeafWork</c> was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The <c>LeafWork</c>'s optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
