namespace JobTrack.Abstractions;

/// <summary>
///     Strongly typed identifier for a <c>job_node</c> row (ADR 0006). Also identifies that node's
///     <c>leaf_work</c> row where one exists, since <c>leaf_work.job_node_id</c> is both its primary
///     key and its foreign key to <c>job_node</c> (schema version 0006) — there is no separate
///     <c>LeafWorkId</c>.
/// </summary>
public readonly record struct JobNodeId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
