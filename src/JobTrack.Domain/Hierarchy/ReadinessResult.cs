namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     The outcome of a readiness check (spec §6): whether every prerequisite attached directly to
///     the checked node or to any of its ancestors is satisfied, and the complete diagnostic set of
///     blockers when it is not.
/// </summary>
public sealed record ReadinessResult(bool IsReady, EquatableArray<UnsatisfiedPrerequisite> Blockers);
