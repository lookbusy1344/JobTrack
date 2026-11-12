namespace JobTrack.TestSupport;

/// <summary>Bounded waits on a task under observation, for concurrency tests.</summary>
public static class TaskObservation
{
	/// <summary>
	///     Waits up to <paramref name="timeout" /> for <paramref name="task" /> and reports whether it finished.
	///     A fault or cancellation counts as finished and is left on the task for the caller to observe.
	///     The wait's timer stops as soon as the task finishes (CA2027).
	/// </summary>
	public static async Task<bool> CompletesWithinAsync(this Task task, TimeSpan timeout)
	{
		ArgumentNullException.ThrowIfNull(task);
		await task.WaitAsync(timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		return task.IsCompleted;
	}
}
