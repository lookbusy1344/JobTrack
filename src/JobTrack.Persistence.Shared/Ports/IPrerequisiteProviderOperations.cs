namespace JobTrack.Persistence.Shared.Ports;

using Abstractions;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     Everything <see cref="PrerequisiteQueryPort" /> cannot express in provider-agnostic LINQ/EF, and
///     nothing else.
/// </summary>
internal interface IPrerequisiteProviderOperations : IProviderReadOperations
{
	/// <summary>
	///     Whether any direct dependent of <paramref name="requiredJobId" />, or a leaf below it,
	///     currently has active work. PostgreSQL calls its source-controlled
	///     <c>jobtrack_has_active_dependent_work</c> function; SQLite walks the dependent subtrees with a
	///     recursive CTE.
	/// </summary>
	Task<bool> HasActiveDependentWorkAsync(DbContext context, JobNodeId requiredJobId, CancellationToken cancellationToken);
}
