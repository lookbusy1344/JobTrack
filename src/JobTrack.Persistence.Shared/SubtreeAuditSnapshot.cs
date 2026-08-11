namespace JobTrack.Persistence.Shared;

using System.Globalization;

/// <summary>
///     Builds the <c>beforeData</c> an <c>delete-subtree</c> audit event carries (ADR 0061). Unlike
///     every other audited mutation, the rows this describes will not exist for anyone to look up
///     afterwards, so the summary is the only surviving record of what was destroyed — it therefore
///     captures each node's description and outcome, not merely counts.
/// </summary>
internal static class SubtreeAuditSnapshot
{
	/// <summary>Flattens <paramref name="impact" /> into the audit event's flat-JSON shape.</summary>
	public static Dictionary<string, string?> Create(SubtreeImpactData impact) => new() {
		["root_id"] = impact.RootId.Value.ToString(CultureInfo.InvariantCulture),
		["node_count"] = impact.NodeCount.ToString(CultureInfo.InvariantCulture),
		["leaf_work_count"] = impact.LeafWorkCount.ToString(CultureInfo.InvariantCulture),
		["work_session_count"] = impact.WorkSessionCount.ToString(CultureInfo.InvariantCulture),
		["total_worked_duration"] = impact.TotalWorkedDuration.ToString(),
		["internal_prerequisite_edge_count"] = impact.InternalPrerequisiteEdgeCount.ToString(CultureInfo.InvariantCulture),
		["job_request_count"] = impact.JobRequestCount.ToString(CultureInfo.InvariantCulture),
		["external_prerequisite_edges"] = string.Join(
			"; ", impact.ExternalPrerequisiteEdges.Select(e => $"{e.FromId.Value}->{e.ToId.Value}")),
		["nodes"] = string.Join(
			"; ",
			impact.Nodes.Select(n => string.Create(
				CultureInfo.InvariantCulture,
				$"{n.Id.Value}@{n.Depth}:{n.Description}:{n.Achievement?.ToString() ?? "-"}:{n.WorkSessionCount}"))),
	};
}
