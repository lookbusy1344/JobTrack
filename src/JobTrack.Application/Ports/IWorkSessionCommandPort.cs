namespace JobTrack.Application.Ports;

/// <summary>
///     The persistence-owned port backing <see cref="IWorkCommands" /> (plan §7.3 step 6). Each method
///     is one atomic transaction: the implementation reloads the actor's current roles and whether the
///     session is (or will be) their own, and applies <see cref="Domain.Authorization.WorkSessionAccessPolicy" />
///     itself before writing — the same shape as <see cref="IJobNodeCommandPort" />'s own mutations.
///     <see cref="StartSessionAsync" /> additionally rechecks the leaf's prerequisite readiness inside
///     the same transaction (spec §6: "the start and completion commands shall recheck prerequisites
///     inside their write transaction").
/// </summary>
internal interface IWorkSessionCommandPort
{
	/// <inheritdoc cref="IWorkCommands.StartSessionAsync" />
	Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IWorkCommands.StartWorkAsync" />
	Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IWorkCommands.FinishSessionAsync" />
	Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IWorkCommands.FinishSessionAndUpdateWriteUpAsync" />
	Task<FinishSessionAndUpdateWriteUpResult> FinishSessionAndUpdateWriteUpAsync(
		FinishSessionAndUpdateWriteUpRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IWorkCommands.CorrectSessionAsync" />
	Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IWorkCommands.CompleteLeafAsync" />
	Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IWorkCommands.ReopenAndStartWorkAsync" />
	Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default);
}
