namespace JobTrack.Application.Ports;

/// <summary>
///     The persistence-owned port backing <see cref="IJobCommands" /> (plan §7.3 steps 3–5). Each method
///     is one atomic transaction: the implementation reloads the actor's current roles and ownership
///     facts and applies <see cref="Domain.Authorization.JobNodeAccessPolicy" /> itself before writing —
///     unlike the read-only ports, a mutation cannot safely hand authorization facts back to
///     <see cref="JobCommands" /> to decide after the write has already happened, so the check and the
///     write share one transaction here, the same shape as the bootstrap port's own "already
///     initialised" guard (ADR 0005, ADR 0015).
/// </summary>
internal interface IJobNodeCommandPort
{
	/// <inheritdoc cref="IJobCommands.AddChildAsync" />
	Task<JobNodeResult> AddChildAsync(CreateJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.EditAsync" />
	Task<JobNodeResult> EditAsync(EditJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.MoveAsync" />
	Task<JobNodeResult> MoveAsync(MoveJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.ArchiveAsync" />
	Task<JobNodeResult> ArchiveAsync(ArchiveJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.DeleteAsync" />
	Task DeleteAsync(DeleteJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.AttachLeafWorkAsync" />
	Task<LeafWorkResult> AttachLeafWorkAsync(AttachLeafWorkRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.DecomposeWorkedLeafAsync" />
	Task<DecomposeWorkedLeafResult> DecomposeWorkedLeafAsync(
		DecomposeWorkedLeafRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.AddPrerequisiteAsync" />
	Task AddPrerequisiteAsync(AddPrerequisiteRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.AddPrerequisitesAsync" />
	Task AddPrerequisitesAsync(AddPrerequisitesRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.RemovePrerequisiteAsync" />
	Task RemovePrerequisiteAsync(RemovePrerequisiteRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IJobCommands.PickUpAsync" />
	Task<JobNodeResult> PickUpAsync(PickUpJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Creates <paramref name="request" />'s already-ordered (parents-before-children — see
	///     <see cref="IJobCommands.ImportSubtreeAsync" />, which computes this ordering before calling
	///     the port) node batch, plus every prerequisite edge between them, in one transaction.
	/// </summary>
	Task<ImportSubtreeResult> ImportSubtreeAsync(ImportSubtreeRequest request, CancellationToken cancellationToken = default);
}
