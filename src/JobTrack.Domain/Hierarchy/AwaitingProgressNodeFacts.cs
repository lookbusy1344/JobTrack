namespace JobTrack.Domain.Hierarchy;

using Abstractions;
using NodaTime;

/// <summary>
///     The display/filter/sort facts about one <c>job_node</c> that <see cref="AwaitingProgressCalculator" />
///     needs alongside <see cref="HierarchyNode" /> — deliberately separate from it since these facts
///     (owner, priority, deadlines, archival) are irrelevant to <see cref="AchievementCalculator" />/
///     <see cref="ReadinessCalculator" />'s structural graph walks.
/// </summary>
public sealed record AwaitingProgressNodeFacts(
	JobNodeId Id,
	string Description,
	AppUserId? OwnerUserId,
	Priority Priority,
	Instant? NeededStart,
	Instant? NeededFinish,
	Instant? ArchivedAt);
