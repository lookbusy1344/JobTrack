namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port for the one-time atomic bootstrap (ADR 0005, ADR 0015). Each
///     provider (<c>JobTrack.Persistence.PostgreSql</c>, <c>JobTrack.Persistence.Sqlite</c>) implements
///     the whole transaction — the <c>app_user</c> row, the Identity credential row, the permanent root
///     <c>job_node</c>, and the initialised marker — behind this single call;
///     <see
///         cref="InstallationCommands" />
///     never sees a transaction boundary (plan §7.4: "one logical
///     mutation uses one context/connection and one transaction; the unit of work is internal").
/// </summary>
public interface IInstallationBootstrapPort
{
	/// <summary>
	///     Atomically creates the first administrator, the permanent root job node, and the
	///     initialised-installation marker in a single transaction (ADR 0015).
	/// </summary>
	/// <exception cref="InvariantViolationException">
	///     The installation is already initialised (<c>ConstraintId</c> <c>"installation-already-initialised"</c>).
	/// </exception>
	Task<BootstrapPersistenceResult> BootstrapAsync(
		BootstrapPersistenceRequest request, CancellationToken cancellationToken = default);
}
