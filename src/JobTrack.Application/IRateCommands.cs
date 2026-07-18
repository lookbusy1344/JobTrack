namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Rate commands (plan §7.3 step 9: add user rates and node overrides;
///     docs/api/jobtrack-client-design.md).
/// </summary>
public interface IRateCommands
{
	/// <summary>Adds an effective-dated user cost rate (spec §9.1).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold rate-management permission (see <see cref="Domain.Authorization.RateAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The employee does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     This rate's effective range overlaps another of this employee's cost rates
	///     (<c>ConstraintId</c> <c>"user-cost-rate-overlap"</c>, spec §9.1).
	/// </exception>
	Task<UserCostRateResult> AddUserCostRateAsync(
		AddUserCostRateRequest request, CancellationToken cancellationToken = default);

	/// <summary>Adds an effective-dated node rate override for a worker (spec §9.2).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold rate-management permission (see <see cref="Domain.Authorization.RateAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The employee or the job node does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     This override's effective range overlaps another override for the same node and worker
	///     (<c>ConstraintId</c> <c>"node-rate-override-overlap"</c>, spec §9.2).
	/// </exception>
	Task<NodeRateOverrideResult> AddNodeRateOverrideAsync(
		AddNodeRateOverrideRequest request, CancellationToken cancellationToken = default);

	/// <summary>Corrects a historical user cost rate in place (ADR 0003).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold rate-management permission (see <see cref="Domain.Authorization.RateAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">
	///     The cost rate does not exist, or does not belong to the given <c>UserId</c>.
	/// </exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version does not match the cost rate's current version.</exception>
	/// <exception cref="InvariantViolationException">
	///     The corrected effective range overlaps another of this employee's cost rates
	///     (<c>ConstraintId</c> <c>"user-cost-rate-overlap"</c>, spec §9.1).
	/// </exception>
	Task<UserCostRateResult> CorrectUserCostRateAsync(
		CorrectUserCostRateRequest request, CancellationToken cancellationToken = default);

	/// <summary>Corrects a historical node rate override in place (ADR 0003).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold rate-management permission (see <see cref="Domain.Authorization.RateAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">
	///     The override does not exist, does not belong to the given <c>UserId</c>, or its corrected
	///     job node does not exist.
	/// </exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version does not match the override's current version.</exception>
	/// <exception cref="InvariantViolationException">
	///     The corrected effective range overlaps another override for the same node and worker
	///     (<c>ConstraintId</c> <c>"node-rate-override-overlap"</c>, spec §9.2).
	/// </exception>
	Task<NodeRateOverrideResult> CorrectNodeRateOverrideAsync(
		CorrectNodeRateOverrideRequest request, CancellationToken cancellationToken = default);
}
