namespace JobTrack.Application;

/// <summary>
///     The archive scope of a job-tree browsing/search query (plan §8.5 slice 2). Archival never
///     implies deletion (spec §4.6) — this filter only controls which nodes a listing includes, and
///     never affects a selected node's own ancestor breadcrumb, which always shows real ancestry.
/// </summary>
public enum JobArchiveFilter
{
	/// <summary>Only nodes with no <c>archived_at</c> — the default operational view.</summary>
	ActiveOnly = 0,

	/// <summary>Only nodes with an <c>archived_at</c>.</summary>
	ArchivedOnly = 1,

	/// <summary>Every node regardless of archive state.</summary>
	All = 2,
}
