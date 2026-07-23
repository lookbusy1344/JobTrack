namespace JobTrack.Application;

using Ports;

/// <summary>
///     Implements job-node structural commands (plan §7.3 steps 3–5) by delegating each atomic
///     operation to <see cref="IJobNodeCommandPort" />, which owns authorization and the transaction
///     (see the port's own documentation for why that check cannot safely live here for mutations).
/// </summary>
internal sealed class JobCommands : IJobCommands
{
	private readonly IJobNodeCommandPort _port;

	/// <summary>Creates a <see cref="JobCommands" /> over the given port.</summary>
	public JobCommands(IJobNodeCommandPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
	}

	/// <inheritdoc />
	public Task<JobNodeResult> AddChildAsync(CreateJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ApplicationEnumValidation.ThrowIfInvalid(request.Priority, nameof(request));

		return JobTrackOperation.TraceAsync(
			"jobs.add-child", request.Context, JobTrackOperation.WithNodeId(request.ParentId),
			() => _port.AddChildAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobNodeResult> EditAsync(EditJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ApplicationEnumValidation.ThrowIfInvalid(request.Priority, nameof(request));

		return JobTrackOperation.TraceAsync(
			"jobs.edit", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.EditAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobNodeResult> MoveAsync(MoveJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"jobs.move", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.MoveAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobNodeResult> ArchiveAsync(ArchiveJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"jobs.archive", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.ArchiveAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task DeleteAsync(DeleteJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"jobs.delete", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.DeleteAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<LeafWorkResult> AttachLeafWorkAsync(AttachLeafWorkRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"jobs.attach-leaf-work", request.Context, JobTrackOperation.WithNodeId(request.JobNodeId),
			() => _port.AttachLeafWorkAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<DecomposeWorkedLeafResult> DecomposeWorkedLeafAsync(
		DecomposeWorkedLeafRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		foreach (var child in request.NewChildren) {
			ApplicationEnumValidation.ThrowIfInvalid(child.Priority, nameof(request));
		}

		return JobTrackOperation.TraceAsync(
			"jobs.decompose-worked-leaf", request.Context, JobTrackOperation.WithNodeId(request.LeafNodeId),
			() => _port.DecomposeWorkedLeafAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task AddPrerequisiteAsync(AddPrerequisiteRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"jobs.add-prerequisite", request.Context, JobTrackOperation.WithNodeId(request.DependentJobId),
			() => _port.AddPrerequisiteAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task AddPrerequisitesAsync(AddPrerequisitesRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.Edges.Count == 0) {
			throw new ArgumentException("At least one prerequisite edge is required.", nameof(request));
		}

		return JobTrackOperation.TraceAsync(
			"jobs.add-prerequisites", request.Context, null,
			() => _port.AddPrerequisitesAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task RemovePrerequisiteAsync(RemovePrerequisiteRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"jobs.remove-prerequisite", request.Context, JobTrackOperation.WithNodeId(request.DependentJobId),
			() => _port.RemovePrerequisiteAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<JobNodeResult> PickUpAsync(PickUpJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"jobs.pick-up", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _port.PickUpAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<ImportSubtreeResult> ImportSubtreeAsync(ImportSubtreeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		foreach (var node in request.Nodes) {
			ApplicationEnumValidation.ThrowIfInvalid(node.Priority, nameof(request));
		}

		var orderedRequest = request with { Nodes = SubtreeImportPlanner.BuildCreationOrder(request.Nodes) };

		return JobTrackOperation.TraceAsync(
			"jobs.import-subtree", request.Context, JobTrackOperation.WithNodeId(request.ParentId),
			() => _port.ImportSubtreeAsync(orderedRequest, cancellationToken));
	}
}
