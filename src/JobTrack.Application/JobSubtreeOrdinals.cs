namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Computes ADR 0039 decision 3's ordinal <c>subtreeLft</c>/<c>subtreeRgt</c> span, rebased to 0 at
///     the requested root, from an already-fetched, already-bounded row set -- never a persisted
///     nested-set column (the schema is adjacency-list only). A pre-order/post-order depth-first walk
///     over the rows' <c>ParentId</c> edges, siblings ordered by <c>Id</c> to match the fetch's own order.
/// </summary>
internal static class JobSubtreeOrdinals
{
	public static Dictionary<JobNodeId, (int Lft, int Rgt)> Compute(IReadOnlyList<JobNodeSubtreeRow> rows, JobNodeId rootId)
	{
		var childrenByParent = rows
			.Where(r => r.ParentId is not null)
			.GroupBy(r => r.ParentId!.Value)
			.ToDictionary(g => g.Key, g => g.Select(r => r.Id).OrderBy(id => id.Value).ToList());

		var spans = new Dictionary<JobNodeId, (int Lft, int Rgt)>();
		var counter = 0;
		Visit(rootId);
		return spans;

		void Visit(JobNodeId id)
		{
			var lft = counter++;
			if (childrenByParent.TryGetValue(id, out var children)) {
				foreach (var child in children) {
					Visit(child);
				}
			}

			spans[id] = (lft, counter++);
		}
	}
}
