namespace JobTrack.Application;

using Microsoft.AspNetCore.Identity;
using Ports;

/// <summary>
///     Implements the one-time installation bootstrap (plan §7.3 step 1; ADR 0005, ADR 0015) by
///     hashing the supplied credential and delegating the atomic write to the persistence-owned
///     <see cref="IInstallationBootstrapPort" />.
/// </summary>
public sealed class InstallationCommands : IInstallationCommands
{
	private static readonly BootstrapCredentialSubject CredentialSubject = new();
	private readonly IPasswordHasher<BootstrapCredentialSubject> _passwordHasher;

	private readonly IInstallationBootstrapPort _port;

	/// <summary>Creates an <see cref="InstallationCommands" /> over the given port and password hasher.</summary>
	public InstallationCommands(
		IInstallationBootstrapPort port, IPasswordHasher<BootstrapCredentialSubject> passwordHasher)
	{
		ArgumentNullException.ThrowIfNull(port);
		ArgumentNullException.ThrowIfNull(passwordHasher);

		_port = port;
		_passwordHasher = passwordHasher;
	}

	/// <inheritdoc />
	public Task<BootstrapAdministratorResult> BootstrapAdministratorAsync(
		BootstrapAdministratorRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return BootstrapAdministratorCoreAsync(request, cancellationToken);
	}

	private Task<BootstrapAdministratorResult> BootstrapAdministratorCoreAsync(
		BootstrapAdministratorRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"installation.bootstrap-administrator",
			request.CorrelationId,
			null,
			async () => {
				var passwordHash = _passwordHasher.HashPassword(CredentialSubject, request.Password);
				var securityStamp = Guid.NewGuid().ToString("N");

				var persistenceRequest = new BootstrapPersistenceRequest {
					DisplayName = request.DisplayName,
					IanaTimeZone = request.IanaTimeZone,
					DefaultHourlyRate = request.DefaultHourlyRate ?? EmployeeProvisioningDefaults.HourlyRate,
					UserName = request.UserName,
					PasswordHash = passwordHash,
					SecurityStamp = securityStamp,
				};

				var result = await _port.BootstrapAsync(persistenceRequest, cancellationToken).ConfigureAwait(false);

				return new BootstrapAdministratorResult {
					AdministratorId = result.AdministratorId,
					AdministratorVersion = result.AdministratorVersion,
					RootJobNodeId = result.RootJobNodeId,
					RootVersion = result.RootVersion,
					InitializedAt = result.InitializedAt,
				};
			});
}
