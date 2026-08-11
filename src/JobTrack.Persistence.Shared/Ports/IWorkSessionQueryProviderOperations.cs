namespace JobTrack.Persistence.Shared.Ports;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     Everything <see cref="WorkSessionQueryPort" /> cannot express in provider-agnostic LINQ/EF, and
///     nothing else.
/// </summary>
internal interface IWorkSessionQueryProviderOperations : IProviderReadOperations
{
	/// <summary>
	///     Of <paramref name="leafWorkIds" />, those the actor controls (ADR 0032's ownership walk).
	///     PostgreSQL calls its source-controlled <c>job_node_controlled_leaf_ids</c> set-returning
	///     function; SQLite walks the ownership chain with a recursive CTE.
	/// </summary>
	Task<IReadOnlyList<long>> GetControlledLeafIdsAsync(
		DbContext context, long actorId, IReadOnlyList<long> leafWorkIds, CancellationToken cancellationToken);
}
