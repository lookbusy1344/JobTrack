namespace JobTrack.TestSupport;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Captures every SQL command text a <see cref="Microsoft.EntityFrameworkCore.DbContext" />
///     executes (2026-08-06-cost-read-materialisation-reduction-plan.md Stage 5) -- proves a query
///     projects only the columns it reads, rather than materializing whole entities where narrower
///     column selection is available.
/// </summary>
public sealed class CommandTextCaptureInterceptor : DbCommandInterceptor
{
	private readonly List<string> commandTexts = [];

	public IReadOnlyList<string> CommandTexts => commandTexts;

	public override InterceptionResult<DbDataReader> ReaderExecuting(
		DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
	{
		commandTexts.Add(command.CommandText);
		return base.ReaderExecuting(command, eventData, result);
	}

	public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
		DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
		CancellationToken cancellationToken = default)
	{
		commandTexts.Add(command.CommandText);
		return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
	}
}
