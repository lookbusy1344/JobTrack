namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     The one-time installation bootstrap (plan §15, §7.3 step 1; ADR 0005; ADR 0015). Every other
///     command requires an already-initialised installation; this is the sole exception, since by
///     definition no administrator yet exists to act as caller.
/// </summary>
public interface IInstallationCommands
{
	/// <summary>
	///     Atomically creates the first administrator, the permanent root job node, and the
	///     initialised-installation marker in a single transaction (ADR 0005, ADR 0015).
	/// </summary>
	/// <exception cref="InvariantViolationException">
	///     The installation is already initialised (<c>ConstraintId</c> <c>"installation-already-initialised"</c>).
	/// </exception>
	Task<BootstrapAdministratorResult> BootstrapAdministratorAsync(
		BootstrapAdministratorRequest request, CancellationToken cancellationToken = default);
}
