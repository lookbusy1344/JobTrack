namespace JobTrack.Application;

using Abstractions;
using Domain.Schedules;
using NodaTime;

internal static class EmployeeProvisioningDefaults
{
	public static readonly HourlyRate HourlyRate = new(20m);
	public static readonly LocalDate ScheduleEffectiveStart = new(2020, 1, 1);
	public static readonly LocalTime WorkingDayStart = new(9, 0);
	public static readonly LocalTime WorkingDayEnd = new(17, 0);

	public static ScheduleVersion CreateSchedule(string ianaTimeZone) =>
		new(
			ScheduleZoneId.Resolve(ianaTimeZone),
			ScheduleEffectiveStart,
			null,
			[
				new(IsoDayOfWeek.Monday, WorkingDayStart, WorkingDayEnd),
				new(IsoDayOfWeek.Tuesday, WorkingDayStart, WorkingDayEnd),
				new(IsoDayOfWeek.Wednesday, WorkingDayStart, WorkingDayEnd),
				new(IsoDayOfWeek.Thursday, WorkingDayStart, WorkingDayEnd),
				new(IsoDayOfWeek.Friday, WorkingDayStart, WorkingDayEnd),
			]);
}
