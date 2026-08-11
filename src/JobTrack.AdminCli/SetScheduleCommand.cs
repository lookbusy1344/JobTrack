namespace JobTrack.AdminCli;

using Abstractions;
using Application;
using Domain.Schedules;
using Identity;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using NodaTime.TimeZones;

/// <summary>
///     The <c>set-schedule</c> command: makes one uniform weekly pattern an employee's standing rota,
///     built from a single civil-time interval repeated across the named days.
///     **It corrects rather than adds when it can, and that is the whole point.** Every account is
///     provisioned with a default schedule already — <c>EmployeeProvisioningDefaults</c> gives
///     Mon–Fri 09:00–17:00 from 2020-01-01, open-ended, on both <c>bootstrap</c> and
///     <c>create-employee</c> — so a plain add would always collide with it on the
///     <c>schedule-version-overlap</c> invariant. On a freshly provisioned account the intent is to
///     replace that placeholder, not to record a change of working pattern, so this corrects the
///     existing version in place (ADR 0003) and leaves no misleading history behind.
///     It refuses as soon as the picture is not that simple: more than one version, or any schedule
///     exception, means real history exists and a blunt overwrite could destroy it. Those cases
///     belong in the Rota pages, where the operator can see what they are changing. The uniform-week
///     shape is likewise deliberate — a per-day pattern or an effective end is not this command's job.
/// </summary>
public static class SetScheduleCommand
{
	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		SetScheduleCommandOptions options,
		IClock clock,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(jobTrackClient);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(clock);

		var actor = await userManager.FindByNameAsync(options.ActorUsername);
		if (actor is null) {
			io.WriteError($"No employee account found for actor username '{options.ActorUsername}'.");
			return 1;
		}

		var target = await userManager.FindByNameAsync(options.Username);
		if (target is null) {
			io.WriteError($"No employee account found for username '{options.Username}'.");
			return 1;
		}

		DateTimeZone zone;
		try {
			zone = ScheduleZoneId.Resolve(options.IanaTimeZone);
		}
		catch (DateTimeZoneNotFoundException) {
			io.WriteError($"'{options.IanaTimeZone}' is not a recognized IANA time zone.");
			return 1;
		}

		var context = new CommandContext { Actor = actor.AppUserId, CorrelationId = Guid.NewGuid() };
		var weeklyIntervals = options.Days
			.Select(day => new WeeklyInterval(day, options.Start, options.End))
			.ToArray();

		ScheduleSnapshotResult existing;
		try {
			existing = await jobTrackClient.Query.GetScheduleAsync(
				new() { Context = context, UserId = target.AppUserId }, cancellationToken);
		}
		catch (JobTrackException ex) {
			io.WriteError($"Failed to read the current schedule for '{options.Username}': {ex.Message}");
			return 1;
		}

		if (existing.Exceptions.Count > 0 || existing.Versions.Count > 1) {
			io.WriteError(
				$"'{options.Username}' already has {existing.Versions.Count} schedule version(s) and " +
				$"{existing.Exceptions.Count} exception(s); refusing to overwrite real history. " +
				"Use the Rota pages to change a schedule that is already in use.");
			return 1;
		}

		var current = existing.Versions.Count == 1 ? existing.Versions[0] : null;
		// Keep the provisioned version's own effective start unless one was given: it predates any work
		// (EmployeeProvisioningDefaults uses 2020-01-01), so preserving it keeps every existing session
		// inside covered working time. Today in the schedule's own zone is the fallback for the
		// no-version case -- not the host's today, since a container running UTC would otherwise start a
		// Europe/London rota on the wrong date either side of midnight.
		var effectiveStart = options.EffectiveStart
							 ?? current?.Schedule.EffectiveStart
							 ?? clock.GetCurrentInstant().InZone(zone).Date;
		var schedule = new ScheduleVersion(zone, effectiveStart, current?.Schedule.EffectiveEnd, [.. weeklyIntervals]);

		try {
			if (current is not null) {
				_ = await jobTrackClient.Schedules.CorrectScheduleVersionAsync(
					new() {
						Context = context,
						VersionId = current.Id,
						UserId = target.AppUserId,
						Version = current.Version,
						Reason = "Standing rota set during installation provisioning.",
						Schedule = schedule,
					},
					cancellationToken);
			} else {
				_ = await jobTrackClient.Schedules.AddScheduleVersionAsync(
					new() { Context = context, UserId = target.AppUserId, Schedule = schedule },
					cancellationToken);
			}
		}
		catch (JobTrackException ex) {
			io.WriteError($"Failed to set the schedule for '{options.Username}': {ex.Message}");
			return 1;
		}

		io.WriteLine(
			$"Set the schedule for '{options.Username}' effective {effectiveStart:yyyy-MM-dd} ({zone.Id}): " +
			$"{options.Start:HH:mm}-{options.End:HH:mm} on {string.Join(", ", options.Days)}.");
		return 0;
	}
}
