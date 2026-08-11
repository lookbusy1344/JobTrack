namespace JobTrack.Application.Tests;

using Abstractions;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IInstallationBootstrapPort" /> for application-slice tests
///     (plan §7.3: "write application tests with fake ports, then provider conformance tests using
///     real databases"). Simulates the single-row installation guard (ADR 0015) without a database.
/// </summary>
internal sealed class FakeInstallationBootstrapPort : IInstallationBootstrapPort
{
	private long _nextAppUserId = 1;
	private long _nextJobNodeId = 1;

	public bool IsInitialised { get; private set; }

	public BootstrapPersistenceRequest? LastRequest { get; private set; }

	public Instant InitializedAtToReturn { get; set; } = Instant.FromUtc(2026, 1, 1, 0, 0);

	public Task<BootstrapPersistenceResult> BootstrapAsync(
		BootstrapPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		if (IsInitialised) {
			throw new InvariantViolationException(
				"installation-already-initialised", "The installation is already initialised.");
		}

		LastRequest = request;
		IsInitialised = true;

		var result = new BootstrapPersistenceResult {
			AdministratorId = new(_nextAppUserId++),
			AdministratorVersion = 1,
			RootJobNodeId = new(_nextJobNodeId++),
			RootVersion = 1,
			InitializedAt = InitializedAtToReturn,
		};

		return Task.FromResult(result);
	}
}
