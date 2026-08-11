namespace JobTrack.TestSupport;

using System.Collections;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Records the longest array-typed command parameter seen across every SQL round trip a
///     <see cref="Microsoft.EntityFrameworkCore.DbContext" /> executes
///     (2026-08-06-cost-read-materialisation-reduction-plan.md Stage 3) -- proves a query's
///     `= ANY(array)` parameters stay bounded by something small (requested root/worker count)
///     rather than growing with the size of an already-materialized node set.
/// </summary>
public sealed class MaxArrayParameterLengthInterceptor : DbCommandInterceptor
{
	public int MaxArrayLength { get; private set; }

	public override InterceptionResult<DbDataReader> ReaderExecuting(
		DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
	{
		RecordArrayParameters(command);
		return base.ReaderExecuting(command, eventData, result);
	}

	public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
		DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
		CancellationToken cancellationToken = default)
	{
		RecordArrayParameters(command);
		return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
	}

	private void RecordArrayParameters(DbCommand command)
	{
		foreach (DbParameter parameter in command.Parameters) {
			if (parameter.Value is ICollection collection && collection.Count > MaxArrayLength) {
				MaxArrayLength = collection.Count;
			}
		}
	}
}
