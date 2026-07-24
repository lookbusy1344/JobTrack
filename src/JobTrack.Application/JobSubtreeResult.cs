namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IJobQueries.GetJobSubtreeAsync" />: a bounded Browse subtree (ADR 0039).</summary>
public sealed record JobSubtreeResult
{
	/// <summary>The subtree root this result was fetched for.</summary>
	public required JobNodeId RootId { get; init; }

	/// <summary>
	///     The root's recursively derived achievement when it is a branch or the permanent root;
	///     <see langword="null" /> when the requested root is a leaf.
	/// </summary>
	public BranchAchievement? RootAchievement { get; init; }

	/// <summary>
	///     The root's penny-rounded, level-reconciled total cost (ADR 0002) -- the sum of every leaf cost
	///     in the root's <em>entire</em> subtree, not just the nodes this bounded fetch rendered (ADR
	///     0039 decision 4). <see langword="null" /> when the actor may not view this subtree's cost (
	///     <see
	///         cref="Domain.Authorization.CostAccessPolicy" />
	///     , ADR 0040).
	/// </summary>
	public Money? RootTotal { get; init; }

	/// <summary>
	///     The <c>DateTimeZoneProviders.Tzdb.VersionId</c> in effect when <see cref="RootTotal" /> was
	///     calculated (ADR 0008/0016) -- <see langword="null" /> exactly when <see cref="RootTotal" /> is.
	/// </summary>
	public string? TzdbVersion { get; init; }

	/// <summary>Every node this bounded fetch returned, in <c>Id</c> order (ADR 0039 decisions 1-2).</summary>
	public required EquatableArray<JobSubtreeNodeResult> Nodes { get; init; }
}
