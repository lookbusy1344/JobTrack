namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IJobQueries.GetJobNodeAsync" />: a node plus its root-first ancestor breadcrumb.</summary>
public sealed record JobNodeDetailResult
{
	/// <summary>The requested node's full detail.</summary>
	public required JobNodeResult Node { get; init; }

	/// <summary>The node's ancestors, root-first, excluding the node itself. Empty for the root.</summary>
	public required EquatableArray<JobNodeAncestorResult> Ancestors { get; init; }
}
