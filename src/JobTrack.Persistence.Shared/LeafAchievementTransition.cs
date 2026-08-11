namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     The one place a tracked <see cref="LeafWorkEntity" /> changes achievement. Every command port
///     that advances a leaf -- <c>SetAchievementAsync</c>, <c>StartWorkAsync</c>,
///     <c>ReopenAndStartWorkAsync</c>, <c>CompleteLeafAsync</c>, and <c>AddChildAsync</c>'s
///     create-and-begin-work composite -- routes through <see cref="ApplyAsync" /> rather than
///     reproducing the mutation, its <c>set-achievement</c> audit event, and ADR 0058's
///     auto-acknowledgement locally. Remediation plan §3.2 exists because the create-and-begin-work
///     composite did reproduce that sequence and silently dropped the acknowledgement from it; making
///     the transition itself the shared unit is what stops a future composite doing the same.
/// </summary>
internal static class LeafAchievementTransition
{
	private const string Operation = "set-achievement";
	private const string AchievementField = "achievement";

	/// <summary>
	///     Moves <paramref name="leafWork" /> to <paramref name="newAchievement" /> at
	///     <paramref name="now" />, bumping its concurrency token, queueing the <c>set-achievement</c>
	///     audit event under <paramref name="reason" />, and applying ADR 0058's requester
	///     auto-acknowledgement when the new state is one this ADR treats as substantive engagement.
	///     Caller-side transition legality (<c>AchievementTransitions.IsPermitted</c>), authorization,
	///     readiness, and session state are the calling port's own checks, already made by this point.
	/// </summary>
	public static async Task ApplyAsync(
		DbContext context, LeafWorkEntity leafWork, Achievement newAchievement, AppUserId actorId, Instant now,
		Guid correlationId, string? reason, CancellationToken cancellationToken)
	{
		var previousAchievement = leafWork.Achievement;
		leafWork.Achievement = newAchievement;
		leafWork.ChangedAt = now;
		leafWork.RowVersion += 1;

		AuditEventWriter.Add(
			context, actorId, now, Operation, "leaf_work", leafWork.JobNodeId.Value, correlationId, reason,
			new Dictionary<string, string?> { [AchievementField] = previousAchievement.ToString() },
			new Dictionary<string, string?> { [AchievementField] = newAchievement.ToString() });

		if (TriggersAcknowledgement(newAchievement)) {
			await RequesterRequestAutoAcknowledgement.AcknowledgeIfNeededAsync(
					context, leafWork.JobNodeId, actorId, now, correlationId, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	///     The import variant of <see cref="ApplyAsync" />, for <c>ImportSubtreeAsync</c> replaying an
	///     already-final achievement onto a <c>leaf_work</c> row it created moments earlier in the same
	///     transaction. It sets no concurrency token (the row is still at its initial version) and writes
	///     no <c>set-achievement</c> audit event -- the import records the whole replay as one
	///     <c>import-leaf-work</c> event instead -- but ADR 0058's acknowledgement is the same side
	///     effect: a requester must not see <c>Submitted</c> beside a job the import already recorded as
	///     in progress or finished.
	/// </summary>
	public static async Task ApplyImportedAsync(
		DbContext context, LeafWorkEntity leafWork, Achievement importedAchievement, AppUserId actorId, Instant now,
		Guid correlationId, CancellationToken cancellationToken)
	{
		leafWork.Achievement = importedAchievement;
		leafWork.ChangedAt = now;

		if (TriggersAcknowledgement(importedAchievement)) {
			await RequesterRequestAutoAcknowledgement.AcknowledgeIfNeededAsync(
					context, leafWork.JobNodeId, actorId, now, correlationId, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	///     ADR 0058's trigger set: the first transition into <see cref="Achievement.InProgress" /> or
	///     into any terminal state. Stated here as its own exhaustive switch rather than deferring to
	///     <c>Domain.Hierarchy.AchievementTransitions.IsCompletedState</c>, because this assembly's
	///     dependency boundary is <c>JobTrack.Abstractions</c> only (enforced by
	///     <c>ReusableLibraryDependencyTests</c>); <c>AchievementTriggerSetTests</c> pins the terminal
	///     half against the domain predicate so the two cannot drift.
	/// </summary>
	public static bool TriggersAcknowledgement(Achievement achievement) => achievement switch {
		Achievement.None => false,
		Achievement.Waiting => false,
		Achievement.InProgress => true,
		Achievement.Success => true,
		Achievement.Cancelled => true,
		Achievement.Unsuccessful => true,
		_ => throw new ArgumentOutOfRangeException(nameof(achievement), achievement, "Unrecognized achievement value."),
	};
}
