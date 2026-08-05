namespace JobTrack.AdminCli;

using System.Globalization;
using Abstractions;
using NodaTime;
using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>set-schedule</c> CLI command.</summary>
public sealed record SetScheduleCommandOptions
{
	/// <summary>
	///     IANA time zone offered when <c>--iana-time-zone</c> is omitted, per this deployment's
	///     UK-standard defaulting convention (matches <see cref="BootstrapCommand" /> and
	///     <see cref="CreateEmployeeCommandOptions" />).
	/// </summary>
	private const string DefaultIanaTimeZone = "Europe/London";

	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	/// <summary>
	///     The administrator performing the change. Managing another employee's schedule requires
	///     <see cref="EmployeeRole.Administrator" /> (<see cref="Domain.Authorization.ScheduleAccessPolicy" />),
	///     so this names an existing administrator whose id becomes the command's actor.
	/// </summary>
	public required string ActorUsername { get; init; }

	/// <summary>The employee whose schedule version is being added.</summary>
	public required string Username { get; init; }

	/// <summary>The days the interval recurs on, in the order given. Never empty, never duplicated.</summary>
	public required EquatableArray<IsoDayOfWeek> Days { get; init; }

	/// <summary>The civil-time start of the working interval on each of <see cref="Days" />.</summary>
	public required LocalTime Start { get; init; }

	/// <summary>The civil-time end of the working interval on each of <see cref="Days" />.</summary>
	public required LocalTime End { get; init; }

	/// <summary>The IANA zone the civil times are interpreted in.</summary>
	public required string IanaTimeZone { get; init; }

	/// <summary>
	///     The inclusive local date the version takes effect, or <see langword="null" /> to let the
	///     command resolve today's date in <see cref="IanaTimeZone" /> — which needs a clock, and so is
	///     the command's job rather than argument parsing's.
	/// </summary>
	public LocalDate? EffectiveStart { get; init; }

	/// <summary>
	///     Reads this command's flags from <paramref name="pico" /> and calls
	///     <see cref="PicoArgs.Finished" /> — the caller has already consumed the leading command via
	///     <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static SetScheduleCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = BootstrapCommandOptions.ParseProvider(pico.GetParam("--provider"));
		var connectionString = ConnectionStringSource.Parse(pico);
		var actorUsername = pico.GetParam("--actor");
		var username = pico.GetParam("--username");
		var daysRaw = pico.GetParam("--days");
		var startRaw = pico.GetParam("--start");
		var endRaw = pico.GetParam("--end");
		var ianaTimeZone = pico.GetParamOpt("--iana-time-zone") ?? DefaultIanaTimeZone;
		var effectiveStartRaw = pico.GetParamOpt("--effective-start");
		pico.Finished();

		var start = ParseTime(startRaw, "--start");
		var end = ParseTime(endRaw, "--end");
		if (start == end) {
			throw new AdminCliUsageException("'--start' and '--end' must differ; a zero-length interval is not a working interval.");
		}

		return new() {
			Provider = provider,
			ConnectionString = connectionString,
			ActorUsername = actorUsername,
			Username = username,
			Days = ParseDays(daysRaw),
			Start = start,
			End = end,
			IanaTimeZone = ianaTimeZone,
			EffectiveStart = effectiveStartRaw is null ? null : ParseEffectiveStart(effectiveStartRaw),
		};
	}

	private static EquatableArray<IsoDayOfWeek> ParseDays(string daysRaw)
	{
		var names = daysRaw.Split(',', StringSplitOptions.TrimEntries);
		var days = new List<IsoDayOfWeek>(names.Length);

		foreach (var name in names) {
			if (!ScheduleDayNames.TryParse(name, out var day)) {
				throw new AdminCliUsageException(
					$"Invalid --days entry '{name}'; expected a comma-separated list of day names (e.g. Mon,Tue or Monday,Tuesday).");
			}

			if (days.Contains(day)) {
				throw new AdminCliUsageException($"Duplicate --days entry '{name}'; each day may appear only once.");
			}

			days.Add(day);
		}

		return days.Count == 0
			? throw new AdminCliUsageException("'--days' must name at least one day.")
			: new([.. days]);
	}

	/// <summary>
	///     Whole-second 24-hour forms only. <see cref="Domain.Schedules.WeeklyInterval" /> rejects a
	///     sub-second component outright (SQLite's tick-of-day and PostgreSQL's microsecond time agree at
	///     second resolution but not below it), so refusing it here turns what would otherwise surface as
	///     an <see cref="ArgumentException" /> from the domain into an ordinary usage error. The formats
	///     are built per call rather than held in a static array, which would freeze only the reference.
	/// </summary>
	private static LocalTime ParseTime(string raw, string flag) =>
		TimeOnly.TryParseExact(raw, ["HH\\:mm", "HH\\:mm\\:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
			? new LocalTime(time.Hour, time.Minute, time.Second)
			: throw new AdminCliUsageException(
				$"Invalid {flag} value '{raw}'; expected a whole-second 24-hour wall-clock time such as 09:00 or 09:00:30.");

	private static LocalDate ParseEffectiveStart(string raw) =>
		DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
			? new LocalDate(date.Year, date.Month, date.Day)
			: throw new AdminCliUsageException($"Invalid --effective-start value '{raw}'; expected an ISO date such as 2026-03-01.");
}
