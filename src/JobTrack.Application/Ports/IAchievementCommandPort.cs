namespace JobTrack.Application.Ports;

/// <summary>
///     The persistence-owned port backing <see cref="IWorkCommands.SetAchievementAsync" /> (plan §7.3
///     step 7). One atomic transaction: the implementation reloads the actor's current roles and
///     ownership fact, applies <see cref="Domain.Authorization.AchievementAccessPolicy" /> and
///     <see cref="Domain.Hierarchy.AchievementTransitions" /> itself, and — for a transition into a
///     completed state — rechecks prerequisite readiness, the same shape as
///     <see cref="IWorkSessionCommandPort.StartSessionAsync" />.
/// </summary>
internal interface IAchievementCommandPort
{
	/// <inheritdoc cref="IWorkCommands.SetAchievementAsync" />
	Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default);
}
