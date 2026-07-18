namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IJobCommands.DecomposeWorkedLeafAsync" />.</summary>
public sealed record DecomposeWorkedLeafResult
{
	/// <summary>The former leaf, now the branch parent of every child below.</summary>
	public required JobNodeId BranchId { get; init; }

	/// <summary>The branch's optimistic-concurrency version after conversion.</summary>
	public required long BranchVersion { get; init; }

	/// <summary>The new child that inherited the existing <c>LeafWork</c> and sessions.</summary>
	public required JobNodeId ExistingWorkChildId { get; init; }

	/// <summary>The newly identified additional children, in the order supplied.</summary>
	public required EquatableArray<JobNodeId> NewChildIds { get; init; }
}
