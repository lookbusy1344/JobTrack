namespace JobTrack.Web;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Domain.Schedules;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

internal static partial class JobTrackApi
{
	private static async Task<IResult> GetScheduleAsync(
		long userId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetScheduleAsync(new() {
				Context = context,
				UserId = new(userId),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddScheduleVersionAsync(
		long userId,
		[FromBody] AddScheduleVersionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var zone = ScheduleZoneId.Resolve(request.IanaTimeZone);
			var weeklyIntervals = request.WeeklyIntervals
										 .Select(interval => new WeeklyInterval(
											 ToIsoDayOfWeek(interval.Day),
											 new(interval.Start.Hour, interval.Start.Minute, interval.Start.Second),
											 new(interval.End.Hour, interval.End.Minute, interval.End.Second)))
										 .ToArray();

			var result = await jobTrackClient.Schedules.AddScheduleVersionAsync(new() {
				Context = context,
				UserId = new(userId),
				Schedule = new(
					zone,
					new(request.EffectiveStart.Year, request.EffectiveStart.Month, request.EffectiveStart.Day),
					request.EffectiveEnd.HasValue
						? new LocalDate(request.EffectiveEnd.Value.Year, request.EffectiveEnd.Value.Month, request.EffectiveEnd.Value.Day)
						: null,
					[.. weeklyIntervals]),
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/schedule", Map(result));
		});
	}

	private static async Task<IResult> CorrectScheduleVersionAsync(
		long userId,
		long versionId,
		[FromBody] CorrectScheduleVersionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var zone = ScheduleZoneId.Resolve(request.IanaTimeZone);
			var weeklyIntervals = request.WeeklyIntervals
										 .Select(interval => new WeeklyInterval(
											 ToIsoDayOfWeek(interval.Day),
											 new(interval.Start.Hour, interval.Start.Minute, interval.Start.Second),
											 new(interval.End.Hour, interval.End.Minute, interval.End.Second)))
										 .ToArray();

			var result = await jobTrackClient.Schedules.CorrectScheduleVersionAsync(new() {
				Context = context,
				VersionId = new(versionId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Schedule = new(
					zone,
					new(request.EffectiveStart.Year, request.EffectiveStart.Month, request.EffectiveStart.Day),
					request.EffectiveEnd.HasValue
						? new LocalDate(request.EffectiveEnd.Value.Year, request.EffectiveEnd.Value.Month, request.EffectiveEnd.Value.Day)
						: null,
					[.. weeklyIntervals]),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CorrectScheduleExceptionAsync(
		long userId,
		long exceptionId,
		[FromBody] CorrectScheduleExceptionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Schedules.CorrectScheduleExceptionAsync(new() {
				Context = context,
				ExceptionId = new(exceptionId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Entry = new(
					request.Effect,
					new(
						Instant.FromDateTimeOffset(request.Start),
						Instant.FromDateTimeOffset(request.End)),
					request.RateOverrideAmountPerHour.HasValue ? new HourlyRate(request.RateOverrideAmountPerHour.Value) : null),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddScheduleExceptionAsync(
		long userId,
		[FromBody] AddScheduleExceptionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Schedules.AddScheduleExceptionAsync(new() {
				Context = context,
				UserId = new(userId),
				Entry = new(
					request.Effect,
					new(
						Instant.FromDateTimeOffset(request.Start),
						Instant.FromDateTimeOffset(request.End)),
					request.RateOverrideAmountPerHour.HasValue ? new HourlyRate(request.RateOverrideAmountPerHour.Value) : null),
				Reason = request.Reason,
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/schedule", Map(result));
		});
	}

	private static ScheduleResponse Map(ScheduleSnapshotResult result) =>
		new() {
			Versions = [.. result.Versions.Select(Map)],
			Exceptions = [.. result.Exceptions.Select(Map)],
		};

	private static ScheduleVersionResponse Map(ScheduleVersionResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			IanaTimeZone = result.Schedule.Zone.Id,
			EffectiveStart = ToDateOnly(result.Schedule.EffectiveStart),
			EffectiveEnd = result.Schedule.EffectiveEnd.HasValue ? ToDateOnly(result.Schedule.EffectiveEnd.Value) : null,
			WeeklyIntervals = [
				.. result.Schedule.WeeklyIntervals.Select(interval =>
					new WeeklyIntervalResponse {
						Day = ToDayOfWeek(interval.Day), Start = ToTimeOnly(interval.Start), End = ToTimeOnly(interval.End),
					}),
			],
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static ScheduleExceptionResponse Map(ScheduleExceptionResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			Effect = result.Entry.Effect,
			Start = result.Entry.Interval.Start.ToDateTimeOffset(),
			End = result.Entry.Interval.End.ToDateTimeOffset(),
			RateOverrideAmountPerHour = result.Entry.RateOverride?.AmountPerHour,
			Reason = result.Reason,
			CreatedByUserId = result.CreatedBy.Value,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static DateOnly ToDateOnly(LocalDate date) => new(date.Year, date.Month, date.Day);

	private static TimeOnly ToTimeOnly(LocalTime time) => new(time.Hour, time.Minute, time.Second);

	private static IsoDayOfWeek ToIsoDayOfWeek(DayOfWeek day) => day switch {
		DayOfWeek.Monday => IsoDayOfWeek.Monday,
		DayOfWeek.Tuesday => IsoDayOfWeek.Tuesday,
		DayOfWeek.Wednesday => IsoDayOfWeek.Wednesday,
		DayOfWeek.Thursday => IsoDayOfWeek.Thursday,
		DayOfWeek.Friday => IsoDayOfWeek.Friday,
		DayOfWeek.Saturday => IsoDayOfWeek.Saturday,
		DayOfWeek.Sunday => IsoDayOfWeek.Sunday,
		_ => throw new ArgumentOutOfRangeException(nameof(day), day, "A weekly interval must specify a real day."),
	};

	private static DayOfWeek ToDayOfWeek(IsoDayOfWeek day) => day switch {
		IsoDayOfWeek.Monday => DayOfWeek.Monday,
		IsoDayOfWeek.Tuesday => DayOfWeek.Tuesday,
		IsoDayOfWeek.Wednesday => DayOfWeek.Wednesday,
		IsoDayOfWeek.Thursday => DayOfWeek.Thursday,
		IsoDayOfWeek.Friday => DayOfWeek.Friday,
		IsoDayOfWeek.Saturday => DayOfWeek.Saturday,
		IsoDayOfWeek.Sunday => DayOfWeek.Sunday,
		_ => throw new ArgumentOutOfRangeException(nameof(day), day, "A weekly interval must specify a real day."),
	};

	internal sealed class ScheduleResponse
	{
		public required ScheduleVersionResponse[] Versions { get; init; }

		public required ScheduleExceptionResponse[] Exceptions { get; init; }
	}

	internal sealed class ScheduleVersionResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required string IanaTimeZone { get; init; }

		public required DateOnly EffectiveStart { get; init; }

		public DateOnly? EffectiveEnd { get; init; }

		public required WeeklyIntervalResponse[] WeeklyIntervals { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class WeeklyIntervalResponse
	{
		public required DayOfWeek Day { get; init; }

		public required TimeOnly Start { get; init; }

		public required TimeOnly End { get; init; }
	}

	internal sealed class ScheduleExceptionResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required ScheduleExceptionEffect Effect { get; init; }

		public required DateTimeOffset Start { get; init; }

		public required DateTimeOffset End { get; init; }

		public decimal? RateOverrideAmountPerHour { get; init; }

		public required string Reason { get; init; }

		public required long CreatedByUserId { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class AddScheduleVersionBody
	{
		[Required] public required string IanaTimeZone { get; init; }

		public required DateOnly EffectiveStart { get; init; }

		public DateOnly? EffectiveEnd { get; init; }

		[Required] public required WeeklyIntervalBody[] WeeklyIntervals { get; init; }
	}

	internal sealed class WeeklyIntervalBody
	{
		public required DayOfWeek Day { get; init; }

		public required TimeOnly Start { get; init; }

		public required TimeOnly End { get; init; }
	}

	internal sealed class AddScheduleExceptionBody
	{
		public required ScheduleExceptionEffect Effect { get; init; }

		public required DateTimeOffset Start { get; init; }

		public required DateTimeOffset End { get; init; }

		public decimal? RateOverrideAmountPerHour { get; init; }

		[Required] public required string Reason { get; init; }
	}

	internal sealed class CorrectScheduleVersionBody
	{
		[Required] public required string IanaTimeZone { get; init; }

		public required DateOnly EffectiveStart { get; init; }

		public DateOnly? EffectiveEnd { get; init; }

		[Required] public required WeeklyIntervalBody[] WeeklyIntervals { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class CorrectScheduleExceptionBody
	{
		public required ScheduleExceptionEffect Effect { get; init; }

		public required DateTimeOffset Start { get; init; }

		public required DateTimeOffset End { get; init; }

		public decimal? RateOverrideAmountPerHour { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	private static void MapScheduleEndpoints(this RouteGroupBuilder api)
	{
		_ = api.MapGet("/employees/{userId:long}/schedule", GetScheduleAsync)
			   .RequireAuthorization(JobTrackPolicyNames.ScheduleAdministration)
			   .WithName("GetEmployeeSchedule")
			   .WithSummary("Get one employee's schedule versions and exceptions (bounded; see plan §3.1).")
			   .Produces<ScheduleResponse>()
			   .ProducesProblem(StatusCodes.Status400BadRequest)
			   .ProducesProblem(StatusCodes.Status401Unauthorized)
			   .ProducesProblem(StatusCodes.Status403Forbidden)
			   .ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPost("/employees/{userId:long}/schedule/versions", AddScheduleVersionAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.ScheduleAdministration,
				   "AddScheduleVersion",
				   "Add an effective-dated schedule version for one employee.")
			   .Produces<ScheduleVersionResponse>(StatusCodes.Status201Created);

		_ = api.MapPost("/employees/{userId:long}/schedule/exceptions", AddScheduleExceptionAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.ScheduleAdministration,
				   "AddScheduleException",
				   "Add a dated schedule exception for one employee.")
			   .Produces<ScheduleExceptionResponse>(StatusCodes.Status201Created);

		_ = api.MapPost("/employees/{userId:long}/schedule/versions/{versionId:long}/correct", CorrectScheduleVersionAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.ScheduleAdministration,
				   "CorrectScheduleVersion",
				   "Correct a historical schedule version's effective range, zone, and weekly intervals, with an audited reason.")
			   .Produces<ScheduleVersionResponse>();

		_ = api.MapPost("/employees/{userId:long}/schedule/exceptions/{exceptionId:long}/correct", CorrectScheduleExceptionAsync)
			   .WithStandardWriteContract(
				   JobTrackPolicyNames.ScheduleAdministration,
				   "CorrectScheduleException",
				   "Correct a historical schedule exception's effect, interval, and rate override, with an audited reason.")
			   .Produces<ScheduleExceptionResponse>();
	}
}
