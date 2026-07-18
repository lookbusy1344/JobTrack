namespace JobTrack.Application.Ports;

/// <summary>
///     The persistence-owned port backing <see cref="IRateCommands" /> (plan §7.3 step 9). Each method
///     is one atomic transaction: the implementation reloads the actor's current roles and applies
///     <see cref="Domain.Authorization.RateAccessPolicy" /> itself before writing — the same
///     mutation-safety shape as the other command ports.
/// </summary>
public interface IRateCommandPort
{
	/// <inheritdoc cref="IRateCommands.AddUserCostRateAsync" />
	Task<UserCostRateResult> AddUserCostRateAsync(
		AddUserCostRateRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRateCommands.AddNodeRateOverrideAsync" />
	Task<NodeRateOverrideResult> AddNodeRateOverrideAsync(
		AddNodeRateOverrideRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRateCommands.CorrectUserCostRateAsync" />
	Task<UserCostRateResult> CorrectUserCostRateAsync(
		CorrectUserCostRateRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IRateCommands.CorrectNodeRateOverrideAsync" />
	Task<NodeRateOverrideResult> CorrectNodeRateOverrideAsync(
		CorrectNodeRateOverrideRequest request, CancellationToken cancellationToken = default);
}
