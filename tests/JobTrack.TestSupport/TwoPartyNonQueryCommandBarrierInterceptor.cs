namespace JobTrack.TestSupport;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Releases two matching EF non-query commands only after both have reached the execution boundary.</summary>
public sealed class TwoPartyNonQueryCommandBarrierInterceptor(Func<string, bool> shouldSynchronize) : DbCommandInterceptor
{
	private const int ParticipantCount = 2;

	private readonly TaskCompletionSource _bothReached =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	private int _participants;

	/// <inheritdoc />
	public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
		DbCommand command,
		CommandEventData eventData,
		InterceptionResult<int> result,
		CancellationToken cancellationToken = default)
	{
		if (shouldSynchronize(command.CommandText)) {
			if (Interlocked.Increment(ref _participants) == ParticipantCount) {
				_bothReached.TrySetResult();
			}

			await _bothReached.Task.WaitAsync(cancellationToken);
		}

		return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
	}
}
