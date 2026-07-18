namespace JobTrack.TestSupport;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Counts SQL round trips a <see cref="Microsoft.EntityFrameworkCore.DbContext" /> executes (Stage 6
///     efficiency guards, ADR 0039 / 2026-07-15 plan §5) -- proves a query stays at a fixed number of
///     round trips as a tree grows wider or deeper, rather than scaling per row (N+1).
/// </summary>
public sealed class CommandCountInterceptor : DbCommandInterceptor
{
	public int Count { get; private set; }

	public override InterceptionResult<DbDataReader> ReaderExecuting(
		DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
	{
		Count++;
		return base.ReaderExecuting(command, eventData, result);
	}

	public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
		DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
		CancellationToken cancellationToken = default)
	{
		Count++;
		return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
	}
}
