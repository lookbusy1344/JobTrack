namespace JobTrack.Persistence.Shared;

using NodaTime;

/// <summary>
///     Wraps the injected <see cref="IClock" /> (the production default is a <see cref="SystemClock" />
///     singleton, per ADR 0016) and truncates every reading to microsecond precision --
///     PostgreSQL's <c>timestamptz</c> columns store no finer than that, while
///     <see cref="SystemClock" /> readings routinely carry sub-microsecond (100ns-tick) noise.
///     Without this, a value handed back from a write (read off
///     the still-tracked in-memory entity, before any round trip) can carry precision a later,
///     genuinely-reread value from the same row can never reproduce -- two readings of one
///     unchanged column comparing unequal. Each PostgreSQL command/query port wraps its injected
///     clock with this in its own constructor, so the guarantee holds regardless of who constructs
///     the port -- the production composition root or a test that builds one directly.
/// </summary>
internal sealed class MicrosecondTruncatingClock(IClock inner) : IClock
{
	private const long TicksPerMicrosecond = NodaConstants.TicksPerSecond / 1_000_000;

	/// <summary>The current instant, truncated down to the nearest whole microsecond.</summary>
	public Instant GetCurrentInstant()
	{
		var ticks = inner.GetCurrentInstant().ToUnixTimeTicks();
		return Instant.FromUnixTimeTicks(ticks - (ticks % TicksPerMicrosecond));
	}
}
