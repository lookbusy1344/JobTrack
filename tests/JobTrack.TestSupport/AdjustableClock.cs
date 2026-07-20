namespace JobTrack.TestSupport;

using NodaTime;

/// <summary>An explicitly controlled clock that also records how often an operation reads it.</summary>
public sealed class AdjustableClock(Instant current) : IClock
{
	public Instant Current { get; set; } = current;

	public int ReadCount { get; private set; }

	public Instant GetCurrentInstant()
	{
		ReadCount++;
		return Current;
	}

	public void ResetReadCount() => ReadCount = 0;
}
