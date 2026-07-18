namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Employee role-assignment commands (plan §8.3): granting and revoking the six baseline
///     authorization roles for an employee. Administrator-only (spec §7.1: "Only administrators may
///     edit employee accounts or global role assignments") -- except <see cref="SetHomeNodeAsync" />,
///     which every employee may call for their own account, carrying no ownership or authorization
///     weight of its own.
/// </summary>
public interface IEmployeeCommands
{
	/// <summary>
	///     Provisions a new employee account holding exactly one initial role (ADR 0023). Starts
	///     enabled with <c>requires_password_change</c> set, mirroring <see cref="ResetPasswordAsync" />.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="InvariantViolationException">
	///     The username is already taken (<c>ConstraintId</c> <c>"employee-username-already-taken"</c>).
	/// </exception>
	Task<AccountStateResult> CreateEmployeeAsync(
		CreateEmployeeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Grants a role to an employee. Idempotent: granting a role the employee already holds is a
	///     no-op that returns their current role membership without writing an audit event.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<EmployeeRolesResult> AssignRoleAsync(
		AssignEmployeeRoleRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Revokes a role from an employee. Idempotent: revoking a role the employee does not hold is a
	///     no-op that returns their current role membership without writing an audit event.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<EmployeeRolesResult> RevokeRoleAsync(
		RevokeEmployeeRoleRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Enables or disables an employee's account. Idempotent: setting a value the account already
	///     has is a no-op that returns current account state without writing an audit event. Disabling
	///     an account revokes its security stamp, ending any live session on its next request (spec
	///     §7.1: "security-stamp revocation after disablement").
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<AccountStateResult> SetEnabledAsync(
		SetEmployeeEnabledRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Sets an employee's default hourly rate, used only when no effective-dated user rate,
	///     node-rate override, or priced additive schedule exception has higher precedence.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<EmployeeProfileResult> SetDefaultHourlyRateAsync(
		SetEmployeeDefaultHourlyRateRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Sets a new credential for an employee's account, forcing a password change at next sign-in
	///     and revoking the account's security stamp (spec §7.1: "security-stamp revocation after ...
	///     reset").
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<AccountStateResult> ResetPasswordAsync(
		ResetEmployeePasswordRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Clears an employee's TOTP two-factor enrolment and revokes the account's security stamp
	///     (ADR 0037), for when they have lost their authenticator device or an administrator otherwise
	///     needs to force re-enrolment. Not idempotent: always rotates the security stamp and writes an
	///     audit event, even if two-factor was already disabled, matching <see cref="ResetPasswordAsync" />.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<AccountStateResult> ResetTwoFactorAsync(
		ResetEmployeeTwoFactorRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Sets the node the calling employee lands on after login instead of the tree root, or clears
	///     it back to root when <see cref="SetHomeNodeRequest.NodeId" /> is <see langword="null" />. Acts
	///     only on <see cref="CommandContext.Actor" />'s own account -- there is no administrator path to
	///     set another employee's home node, and none is needed since this carries no ownership or
	///     authorization weight.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The given node does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The given node is a <see cref="Abstractions.NodeKind.Leaf" /> (<c>ConstraintId</c>
	///     <c>"home-node-must-not-be-leaf"</c>).
	/// </exception>
	Task<EmployeeProfileResult> SetHomeNodeAsync(
		SetHomeNodeRequest request, CancellationToken cancellationToken = default);
}
