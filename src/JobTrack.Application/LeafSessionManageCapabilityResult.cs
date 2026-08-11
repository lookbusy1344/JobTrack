namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     One leaf's result from <see cref="IJobQueries.GetSessionManageCapabilitiesAsync" />: whether the
///     querying actor may currently manage (start/finish on behalf of another worker, or their own)
///     sessions on this leaf, per <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" />.
///     This is a rendering hint only — the authoritative gate remains the command itself, which
///     reloads roles and ownership at write time (ADR 0044 Stage 4: "do not trust the capability for
///     authorization").
/// </summary>
public sealed record LeafSessionManageCapabilityResult
{
	/// <summary>The leaf this result describes.</summary>
	public required JobNodeId LeafWorkId { get; init; }

	/// <summary>Whether the actor may currently manage sessions on this leaf.</summary>
	public required bool CanManage { get; init; }
}
