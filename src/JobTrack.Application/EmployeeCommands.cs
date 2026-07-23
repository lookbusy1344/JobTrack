namespace JobTrack.Application;

using Microsoft.AspNetCore.Identity;
using Ports;

/// <summary>
///     Implements employee account-management commands (plan §8.3) by delegating each atomic operation
///     to <see cref="IEmployeeCommandPort" />, which owns authorization and the transaction.
/// </summary>
internal sealed class EmployeeCommands : IEmployeeCommands
{
	private static readonly EmployeeCredentialSubject CredentialSubject = new();
	private readonly IPasswordHasher<EmployeeCredentialSubject> _passwordHasher;

	private readonly IEmployeeCommandPort _port;

	/// <summary>Creates an <see cref="EmployeeCommands" /> over the given port and password hasher.</summary>
	public EmployeeCommands(IEmployeeCommandPort port, IPasswordHasher<EmployeeCredentialSubject> passwordHasher)
	{
		ArgumentNullException.ThrowIfNull(port);
		ArgumentNullException.ThrowIfNull(passwordHasher);

		_port = port;
		_passwordHasher = passwordHasher;
	}

	/// <inheritdoc />
	public Task<AccountStateResult> CreateEmployeeAsync(
		CreateEmployeeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ApplicationEnumValidation.ThrowIfInvalid(request.Role, nameof(request));

		return JobTrackOperation.TraceAsync(
			"employees.create-employee", request.Context, null,
			() => {
				var passwordHash = _passwordHasher.HashPassword(CredentialSubject, request.Password);

				return _port.CreateEmployeeAsync(
					new() {
						Context = request.Context,
						DisplayName = request.DisplayName,
						IanaTimeZone = request.IanaTimeZone,
						DefaultHourlyRate = request.DefaultHourlyRate ?? EmployeeProvisioningDefaults.HourlyRate,
						UserName = request.UserName,
						PasswordHash = passwordHash,
						Role = request.Role,
					},
					cancellationToken);
			});
	}

	/// <inheritdoc />
	public Task<EmployeeRolesResult> AssignRoleAsync(
		AssignEmployeeRoleRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ApplicationEnumValidation.ThrowIfInvalid(request.Role, nameof(request));

		return JobTrackOperation.TraceAsync(
			"employees.assign-role", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.AssignRoleAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<EmployeeRolesResult> RevokeRoleAsync(
		RevokeEmployeeRoleRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ApplicationEnumValidation.ThrowIfInvalid(request.Role, nameof(request));

		return JobTrackOperation.TraceAsync(
			"employees.revoke-role", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.RevokeRoleAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<AccountStateResult> SetEnabledAsync(
		SetEmployeeEnabledRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"employees.set-enabled", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.SetEnabledAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<EmployeeProfileResult> SetDefaultHourlyRateAsync(
		SetEmployeeDefaultHourlyRateRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"employees.set-default-hourly-rate", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.SetDefaultHourlyRateAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<AccountStateResult> ResetPasswordAsync(
		ResetEmployeePasswordRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"employees.reset-password", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => {
				var passwordHash = _passwordHasher.HashPassword(CredentialSubject, request.NewPassword);

				return _port.ResetPasswordAsync(
					new() { Context = request.Context, TargetUserId = request.TargetUserId, PasswordHash = passwordHash },
					cancellationToken);
			});
	}

	/// <inheritdoc />
	public Task<AccountStateResult> ResetTwoFactorAsync(
		ResetEmployeeTwoFactorRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"employees.reset-two-factor", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.ResetTwoFactorAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<EmployeeProfileResult> SetHomeNodeAsync(
		SetHomeNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"employees.set-home-node", request.Context, null,
			() => _port.SetHomeNodeAsync(request, cancellationToken));
	}
}
