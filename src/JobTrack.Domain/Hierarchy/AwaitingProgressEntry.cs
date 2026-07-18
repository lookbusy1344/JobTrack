namespace JobTrack.Domain.Hierarchy;

using Abstractions;
using NodaTime;

/// <summary>
///     One row of <see cref="AwaitingProgressCalculator.GetAwaitingProgress" />. <see cref="Achievement" />
///     is <see langword="null" /> when the leaf has no <c>LeafWork</c> attached yet — it still needs
///     someone to attach work or decompose it further, so it belongs on the list alongside
///     <see cref="Abstractions.Achievement.Waiting" /> and <see cref="Abstractions.Achievement.InProgress" /> leaves.
///     <see cref="IsReady" /> is <see langword="false" /> when the leaf is blocked by an unsatisfied
///     prerequisite (spec §6) — it still appears on the list rather than disappearing, so a caller can
///     render it visibly as blocked instead of actionable.
/// </summary>
public sealed record AwaitingProgressEntry(
	JobNodeId Id,
	JobNodeId? ParentId,
	string Description,
	AppUserId? OwnerUserId,
	Priority Priority,
	Achievement? Achievement,
	Money? Cost,
	Instant? NeededStart,
	Instant? NeededFinish,
	bool IsReady);
