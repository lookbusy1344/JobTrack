namespace JobTrack.TestSupport;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Deterministically pauses the first matching EF reader command until a test releases it.</summary>
public sealed class BlockingReaderCommandInterceptor(Func<string, bool> shouldBlock) : DbCommandInterceptor
{
	private readonly TaskCompletionSource _commandReached =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly TaskCompletionSource _release =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly List<DbTransaction?> _transactions = [];
	private int _hasBlocked;

	/// <summary>Completes when the matching command has reached the interceptor.</summary>
	public Task CommandReached => _commandReached.Task;

	/// <summary>The transaction attached to each intercepted reader command, in execution order.</summary>
	public IReadOnlyList<DbTransaction?> Transactions
	{
		get
		{
			lock (_transactions) {
				return [.. _transactions];
			}
		}
	}

	/// <summary>Allows the paused command to execute.</summary>
	public void Release() => _release.TrySetResult();

	public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
		DbCommand command,
		CommandEventData eventData,
		InterceptionResult<DbDataReader> result,
		CancellationToken cancellationToken = default)
	{
		lock (_transactions) {
			_transactions.Add(command.Transaction);
		}

		if (shouldBlock(command.CommandText) && Interlocked.Exchange(ref _hasBlocked, 1) == 0) {
			_commandReached.TrySetResult();
			await _release.Task.WaitAsync(cancellationToken);
		}

		return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
	}
}
