namespace JobTrack.AdminCli;

using System.Globalization;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using NodaTime;
using NodaTime.Text;

/// <summary>
///     Maps an <c>import-tree</c> JSON row's optional work fields onto an
///     <see cref="ImportSubtreeLeafWorkSpec" />, so a bulk-authored tree can carry the history the
///     author already knows about rather than arriving uniformly untouched.
///     <para>
///         A row spells its work one of two ways, never both. The <em>relative</em> spelling —
///         <c>open</c> and optionally <c>closed</c> — states how long before the import each event
///         happened (<c>open: "2 days"</c>, <c>closed: "1 day"</c> reads as "started two days ago,
///         closed a day ago"), and is resolved against the single instant the import captured when it
///         began. The <em>absolute</em> spelling — <c>start</c> and optionally <c>end</c> — gives ISO
///         8601 timestamps outright. A row with no finish is still in progress; a row that finishes
///         takes its achievement from <c>outcome</c>, which defaults to success.
///     </para>
/// </summary>
public static partial class JobTreeImportWork
{
	private const int DaysPerWeek = 7;

	/// <summary>
	///     Resolves <paramref name="raw" />'s work fields against <paramref name="importedAt" />, or
	///     returns <see langword="null" /> when the row records no work at all.
	/// </summary>
	/// <param name="raw">The JSON row.</param>
	/// <param name="importedAt">The instant the import captured when it began — relative durations count back from here.</param>
	/// <param name="workedBy">The employee credited with the session; the import's single <c>--username</c> employee.</param>
	/// <exception cref="AdminCliUsageException">
	///     The row mixes the two spellings, closes work it never opened, carries an <c>outcome</c>
	///     without closing, or contains an unparseable duration, timestamp, or outcome.
	/// </exception>
	public static ImportSubtreeLeafWorkSpec? Resolve(JobTreeImportNodeJson raw, Instant importedAt, AppUserId workedBy)
	{
		ArgumentNullException.ThrowIfNull(raw);

		var hasRelative = raw.Open is not null || raw.Closed is not null;
		var hasAbsolute = raw.Start is not null || raw.End is not null;

		if (hasRelative && hasAbsolute) {
			throw new AdminCliUsageException(
				$"Node {raw.Id} cannot mix the relative 'open'/'closed' fields with the absolute 'start'/'end' fields.");
		}

		if (!hasRelative && !hasAbsolute) {
			return raw.Outcome is null
				? null
				: throw new AdminCliUsageException(
					$"Node {raw.Id} has an 'outcome' but records no work; add 'open'/'closed' or 'start'/'end'.");
		}

		var (startedAt, finishedAt) = hasRelative
			? ResolveRelative(raw, importedAt)
			: ResolveAbsolute(raw);

		if (finishedAt is null && raw.Outcome is not null) {
			throw new AdminCliUsageException(
				$"Node {raw.Id} has an 'outcome' but never closes; an unfinished job is always in progress.");
		}

		return new() {
			WorkedByUserId = workedBy,
			StartedAt = startedAt,
			FinishedAt = finishedAt,
			Achievement = finishedAt is null ? Achievement.InProgress : ParseOutcome(raw.Outcome, raw.Id),
		};
	}

	private static (Instant StartedAt, Instant? FinishedAt) ResolveRelative(JobTreeImportNodeJson raw, Instant importedAt)
	{
		if (raw.Open is null) {
			throw new AdminCliUsageException($"Node {raw.Id} has 'closed' but no 'open'; a job cannot close before it opens.");
		}

		return (
			importedAt - ParseDuration(raw.Open, "open", raw.Id),
			raw.Closed is null ? null : importedAt - ParseDuration(raw.Closed, "closed", raw.Id));
	}

	private static (Instant StartedAt, Instant? FinishedAt) ResolveAbsolute(JobTreeImportNodeJson raw)
	{
		if (raw.Start is null) {
			throw new AdminCliUsageException($"Node {raw.Id} has 'end' but no 'start'; a job cannot end before it starts.");
		}

		return (ParseInstant(raw.Start, "start", raw.Id), raw.End is null ? null : ParseInstant(raw.End, "end", raw.Id));
	}

	/// <summary>
	///     Parses "&lt;amount&gt; &lt;unit&gt;" — "2 days", "90 minutes", "1.5 days", "3d" — into an
	///     exact <see cref="Duration" />. The amount is scaled in <see cref="decimal" /> and converted to
	///     whole ticks, never through <see cref="double" />, keeping this off the floating-point path
	///     the duration and money rules exclude.
	/// </summary>
	private static Duration ParseDuration(string text, string fieldName, long nodeId)
	{
		var match = RelativeDurationPattern().Match(text);
		if (!match.Success) {
			throw new AdminCliUsageException(
				$"Node {nodeId} has an unrecognised '{fieldName}' value '{text}'; expected something like "
				+ "\"2 days\", \"90 minutes\", or \"36 hours\" (units: minutes, hours, days, weeks).");
		}

		var amount = decimal.Parse(match.Groups["amount"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
		var ticksPerUnit = match.Groups["unit"].Value.ToLowerInvariant() switch {
			"m" or "min" or "mins" or "minute" or "minutes" => NodaConstants.TicksPerMinute,
			"h" or "hr" or "hrs" or "hour" or "hours" => NodaConstants.TicksPerHour,
			"d" or "day" or "days" => NodaConstants.TicksPerDay,
			"w" or "week" or "weeks" => NodaConstants.TicksPerDay * DaysPerWeek,
			var unit => throw new AdminCliUsageException(
				$"Node {nodeId} has an unrecognised '{fieldName}' unit '{unit}'; expected minutes, hours, days, or weeks."),
		};

		return Duration.FromTicks(decimal.ToInt64(decimal.Round(amount * ticksPerUnit, MidpointRounding.ToEven)));
	}

	/// <summary>
	///     Parses an ISO 8601 timestamp that carries an explicit offset ("2026-07-10T09:00:00Z",
	///     "2026-07-10T09:00:00+01:00"). An offset is required rather than assumed: a bare local time
	///     would silently pick a zone, and the instants recorded here are compared against prerequisite
	///     finish times where an hour's drift changes the answer.
	/// </summary>
	private static Instant ParseInstant(string text, string fieldName, long nodeId)
	{
		var parsed = OffsetDateTimePattern.ExtendedIso.Parse(text);
		return parsed.Success
			? parsed.Value.ToInstant()
			: throw new AdminCliUsageException(
				$"Node {nodeId} has an unrecognised '{fieldName}' value '{text}'; expected an ISO 8601 timestamp with an "
				+ "explicit offset, such as \"2026-07-10T09:00:00Z\" or \"2026-07-10T09:00:00+01:00\".");
	}

	private static Achievement ParseOutcome(string? outcome, long nodeId) => outcome?.ToLowerInvariant() switch {
		null or "success" => Achievement.Success,
		"cancelled" or "canceled" => Achievement.Cancelled,
		"unsuccessful" => Achievement.Unsuccessful,
		_ => throw new AdminCliUsageException(
			$"Node {nodeId} has an unrecognised 'outcome' value '{outcome}'; expected success, cancelled, or unsuccessful."),
	};

	[GeneratedRegex(@"^\s*(?<amount>\d+(?:\.\d+)?)\s*(?<unit>[a-zA-Z]+)\s*$")]
	private static partial Regex RelativeDurationPattern();
}
