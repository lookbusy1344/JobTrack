namespace JobTrack.Application.Ports;

using Abstractions;
using NodaTime;

/// <summary>Result of <see cref="IInstallationBootstrapPort.BootstrapAsync" />.</summary>
internal sealed record BootstrapPersistenceResult
{
	/// <summary>The new administrator's <c>app_user</c> identifier.</summary>
	public required AppUserId AdministratorId { get; init; }

	/// <summary>The new administrator's optimistic-concurrency version.</summary>
	public required long AdministratorVersion { get; init; }

	/// <summary>The permanent root <c>job_node</c> identifier.</summary>
	public required JobNodeId RootJobNodeId { get; init; }

	/// <summary>The permanent root's optimistic-concurrency version.</summary>
	public required long RootVersion { get; init; }

	/// <summary>The captured instant at which the installation became initialised.</summary>
	public required Instant InitializedAt { get; init; }
}
