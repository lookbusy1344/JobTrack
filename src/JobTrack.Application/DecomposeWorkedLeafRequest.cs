namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.DecomposeWorkedLeafAsync" /> (spec §3.5): atomically (1) creates
///     a child for the work already done, inheriting the existing <c>LeafWork</c> and every session
///     unchanged; (2) creates each newly identified child in <see cref="NewChildren" />; and (3) converts
///     <see cref="LeafNodeId" /> into their branch parent. Never used for mere pause/resume.
/// </summary>
public sealed record DecomposeWorkedLeafRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The currently-worked leaf being decomposed.</summary>
	public required JobNodeId LeafNodeId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version of <see cref="LeafNodeId" />.</summary>
	public required long Version { get; init; }

	/// <summary>The description <see cref="LeafNodeId" /> takes on once converted into a branch.</summary>
	public required string BranchDescription { get; init; }

	/// <summary>
	///     The description for the new child that inherits the existing <c>LeafWork</c> and sessions.
	///     That child keeps <see cref="LeafNodeId" />'s current owner — the work itself is unchanged,
	///     only relocated.
	/// </summary>
	public required string ExistingWorkDescription { get; init; }

	/// <summary>The newly identified additional child jobs.</summary>
	public required EquatableArray<NewChildJobSpec> NewChildren { get; init; }
}
