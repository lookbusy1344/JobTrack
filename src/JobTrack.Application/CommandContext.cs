namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     The acting user and correlation identifier carried by every command and query except
///     <see cref="IInstallationCommands.BootstrapAdministratorAsync" />, which by definition precedes
///     any actor's existence (plan §7.1). Reused uniformly for reads as well as writes, rather than a
///     separate read-only context type, so the whole facade shares one context shape.
/// </summary>
public sealed record CommandContext
{
	/// <summary>The <c>app_user</c> performing this operation.</summary>
	public required AppUserId Actor { get; init; }

	/// <summary>Correlates this operation with the audit events and logs it produces.</summary>
	public required Guid CorrelationId { get; init; }
}
