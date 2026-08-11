namespace JobTrack.Persistence.Shared.Ports;

using Abstractions;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     Everything <see cref="JobBrowseQueryPort" /> cannot express in provider-agnostic LINQ/EF, and
///     nothing else. Each provider implements these members; the rest of the port is shared.
/// </summary>
/// <remarks>
///     Keeping the seam an explicit interface rather than a set of overrides is deliberate: this file
///     is the whole answer to "how does PostgreSQL differ from SQLite here?". A member added below is
///     a new divergence between the providers and wants justifying.
/// </remarks>
internal interface IJobBrowseProviderOperations : IProviderReadOperations
{
	/// <summary>
	///     Derives each requested branch's recursive success state in one round trip. The caller keeps
	///     the returned map only for the current subtree request.
	/// </summary>
	Task<IReadOnlyDictionary<long, bool>> GetSubtreeSuccessesAsync(
		DbContext context, IReadOnlyCollection<long> rootIds, CancellationToken cancellationToken);

	/// <summary>
	///     Answers whether every leaf beneath <paramref name="rootId" /> reached
	///     <see cref="Achievement.Success" />. PostgreSQL resolves this with the source-controlled
	///     <c>node_succeeded</c> function; SQLite walks the subtree with a recursive CTE.
	/// </summary>
	Task<bool> IsSubtreeSucceededAsync(DbContext context, long rootId, CancellationToken cancellationToken);
}
