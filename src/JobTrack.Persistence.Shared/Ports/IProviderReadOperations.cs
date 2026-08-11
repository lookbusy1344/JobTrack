namespace JobTrack.Persistence.Shared.Ports;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     The one thing a read-only shared query port cannot do provider-neutrally: open a context. One
///     implementation per provider, shared by all of them.
/// </summary>
/// <remarks>
///     A query port that needs more than this declares its own interface extending this one, so the
///     extra member stays visible as a divergence (ADR 0064) rather than accumulating here.
/// </remarks>
internal interface IProviderReadOperations
{
	/// <summary>Opens a fresh context for a single call, with any provider-required connection setup already applied.</summary>
	DbContext CreateContext();
}
