namespace JobTrack.Database;

/// <summary>
///     ADR 0007's canonical SQLite instant encoding: a signed 64-bit count of
///     100-nanosecond ticks since the Unix epoch, applied uniformly to every
///     temporal column including the schema-deployment record's applied-at
///     timestamp. This tool has no NodaTime/domain dependency, so the
///     conversion is expressed directly against <see cref="DateTimeOffset" />.
/// </summary>
internal static class SqliteInstantEncoding
{
	public static long ToUnixEpochTicks(DateTimeOffset value) =>
		value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;

	public static DateTimeOffset ToDateTimeOffset(long unixEpochTicks) =>
		new(new(DateTime.UnixEpoch.Ticks + unixEpochTicks, DateTimeKind.Utc));
}
