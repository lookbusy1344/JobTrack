namespace JobTrack.AdminCli.Tests;

using Abstractions;
using Application;
using NodaTime;

/// <summary>
///     Mirrors <c>JobTrackClientUsageExampleTests.FakeInstallationCommands</c>
///     (<c>tests/JobTrack.PublicApi.Tests/JobTrackClientUsageExampleTests.cs</c>).
/// </summary>
internal sealed class FakeInstallationCommands(bool alreadyInitialised = false) : IInstallationCommands
{
	public BootstrapAdministratorRequest? ReceivedRequest { get; private set; }

	public Task<BootstrapAdministratorResult> BootstrapAdministratorAsync(
		BootstrapAdministratorRequest request, CancellationToken cancellationToken = default)
	{
		ReceivedRequest = request;

		if (alreadyInitialised) {
			throw new InvariantViolationException("installation-already-initialised", "The installation is already initialised.");
		}

		return Task.FromResult(new BootstrapAdministratorResult {
			AdministratorId = new(1),
			AdministratorVersion = 1,
			RootJobNodeId = new(1),
			RootVersion = 1,
			InitializedAt = Instant.FromUtc(2026, 7, 5, 0, 0),
		});
	}
}
