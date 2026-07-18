namespace JobTrack.Application.Ports;

/// <summary>
///     The persistence-owned port backing <see cref="IEmployeeCommands" /> (plan §8.3). Each method is
///     one atomic transaction: the implementation reloads the actor's current roles and applies
///     <see cref="Domain.Authorization.EmployeeAccessPolicy.CanManageRoles" /> or
///     <see cref="Domain.Authorization.EmployeeAccessPolicy.CanManageAccounts" /> itself before writing —
///     the same mutation-safety shape as the other command ports.
/// </summary>
public interface IEmployeeCommandPort
{
	/// <summary>Persists a new employee account with an already-hashed credential — see <see cref="IEmployeeCommands.CreateEmployeeAsync" />.</summary>
	Task<AccountStateResult> CreateEmployeeAsync(
		CreateEmployeePersistenceRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IEmployeeCommands.AssignRoleAsync" />
	Task<EmployeeRolesResult> AssignRoleAsync(
		AssignEmployeeRoleRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IEmployeeCommands.RevokeRoleAsync" />
	Task<EmployeeRolesResult> RevokeRoleAsync(
		RevokeEmployeeRoleRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IEmployeeCommands.SetEnabledAsync" />
	Task<AccountStateResult> SetEnabledAsync(
		SetEmployeeEnabledRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IEmployeeCommands.SetDefaultHourlyRateAsync" />
	Task<EmployeeProfileResult> SetDefaultHourlyRateAsync(
		SetEmployeeDefaultHourlyRateRequest request, CancellationToken cancellationToken = default);

	/// <summary>Persists an already-hashed credential reset — see <see cref="IEmployeeCommands.ResetPasswordAsync" />.</summary>
	Task<AccountStateResult> ResetPasswordAsync(
		ResetEmployeePasswordPersistenceRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IEmployeeCommands.ResetTwoFactorAsync" />
	Task<AccountStateResult> ResetTwoFactorAsync(
		ResetEmployeeTwoFactorRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IEmployeeCommands.SetHomeNodeAsync" />
	Task<EmployeeProfileResult> SetHomeNodeAsync(
		SetHomeNodeRequest request, CancellationToken cancellationToken = default);
}
