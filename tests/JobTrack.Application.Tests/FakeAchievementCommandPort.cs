namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Authorization;
using Domain.Hierarchy;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IAchievementCommandPort" /> for application-slice tests (plan
///     §7.3: "write application tests with fake ports, then provider conformance tests using real
///     databases"). Shares the <see cref="FakeJobNodeCommandPort" /> graph rather than duplicating
///     node/leaf-work/prerequisite state.
/// </summary>
internal sealed class FakeAchievementCommandPort(FakeJobNodeCommandPort nodePort) : IAchievementCommandPort
{
	public Instant NowToReturn { get; set; } = Instant.FromUtc(2026, 1, 1, 12, 0);

	public async Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default)
	{
		var leafWork = nodePort.FindLeafWork(request.JobNodeId)
					   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");

		var isReopening = AchievementTransitions.IsReopening(leafWork.Achievement, request.NewAchievement);
		var ownsNodeOrAncestor = nodePort.OwnsNodeOrAncestor(request.Context.Actor, request.JobNodeId);

		if (!AchievementAccessPolicy.CanSetAchievement(nodePort.RolesOf(request.Context.Actor), ownsNodeOrAncestor, isReopening)) {
			throw new AuthorizationDeniedException(
				$"Actor {request.Context.Actor} may not change achievement for job node {request.JobNodeId}.");
		}

		if (leafWork.Version != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} but the current version is {leafWork.Version}.");
		}

		if (!AchievementTransitions.IsPermitted(leafWork.Achievement, request.NewAchievement)) {
			throw new InvariantViolationException(
				"achievement-transition-not-permitted",
				$"Cannot transition from {leafWork.Achievement} to {request.NewAchievement}.");
		}

		if (AchievementTransitions.IsCompletedState(request.NewAchievement)) {
			var readinessInputs = await nodePort.GetReadinessInputsAsync(request.JobNodeId, cancellationToken).ConfigureAwait(false);
			var readiness = ReadinessCalculator.IsReady(request.JobNodeId, readinessInputs.NodesById, readinessInputs.Prerequisites);
			if (!readiness.IsReady) {
				throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
			}
		}

		var updated = leafWork with { Achievement = request.NewAchievement, ChangedAt = NowToReturn, Version = leafWork.Version + 1 };
		nodePort.SetLeafWork(updated);

		return updated;
	}
}
