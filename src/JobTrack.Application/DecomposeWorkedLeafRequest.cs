namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.DecomposeWorkedLeafAsync" /> (spec §3.5): atomically (1) if
///     <see cref="LeafNodeId" /> currently has <c>LeafWork</c> attached, creates a child for the work
///     already done, inheriting that <c>LeafWork</c> and every session unchanged -- otherwise skipped,
///     since a bare leaf has no work to carry over (ADR 0067); (2) creates each newly identified child
///     in <see cref="NewChildren" />; and (3) converts <see cref="LeafNodeId" /> into their branch
///     parent. Never used for mere pause/resume.
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
	///     only relocated. Meaningful, and required, only when <see cref="LeafNodeId" /> currently has
	///     <c>LeafWork</c> attached; ignored for a bare leaf (ADR 0067).
	/// </summary>
	public string? ExistingWorkDescription { get; init; }

	/// <summary>
	///     The newly identified additional child jobs. Must be non-empty when <see cref="LeafNodeId" />
	///     is a bare leaf -- otherwise the decomposition would produce a childless branch (ADR 0067).
	/// </summary>
	public required EquatableArray<NewChildJobSpec> NewChildren { get; init; }
}
