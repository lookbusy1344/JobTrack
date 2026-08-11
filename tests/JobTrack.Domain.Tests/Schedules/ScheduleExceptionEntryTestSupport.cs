namespace JobTrack.Domain.Tests.Schedules;

using System.Reflection;
using Abstractions;
using Domain.Intervals;
using Domain.Schedules;

/// <summary>
///     Builds schedule-exception test inputs that cannot be constructed through the public constructor alone.
/// </summary>
internal static class ScheduleExceptionEntryTestSupport
{
	internal static ScheduleExceptionEntry WithEffect(
		ScheduleExceptionEffect effect, WorkInterval interval, HourlyRate? rateOverride = null)
	{
		var placeholderEffect = effect == ScheduleExceptionEffect.RemoveWorkingTime
			? ScheduleExceptionEffect.RemoveWorkingTime
			: ScheduleExceptionEffect.AddWorkingTime;
		var entry = new ScheduleExceptionEntry(
			placeholderEffect,
			interval,
			placeholderEffect == ScheduleExceptionEffect.RemoveWorkingTime ? null : rateOverride);

		typeof(ScheduleExceptionEntry)
			.GetField("<Effect>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(entry, effect);

		return entry;
	}
}
