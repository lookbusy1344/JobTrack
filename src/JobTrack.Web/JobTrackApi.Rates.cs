namespace JobTrack.Web;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

internal static partial class JobTrackApi
{
	private static async Task<IResult> GetRatesAsync(
		long userId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetRatesAsync(new() { Context = context, UserId = new(userId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddUserCostRateAsync(
		long userId,
		[FromBody] AddUserCostRateBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.AddUserCostRateAsync(new() {
				Context = context,
				UserId = new(userId),
				Rate = new(
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/rates", Map(result));
		});
	}

	private static async Task<IResult> CorrectUserCostRateAsync(
		long userId,
		long rateId,
		[FromBody] CorrectUserCostRateBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.CorrectUserCostRateAsync(new() {
				Context = context,
				RateId = new(rateId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Rate = new(
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CorrectNodeRateOverrideAsync(
		long userId,
		long overrideId,
		[FromBody] CorrectNodeRateOverrideBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.CorrectNodeRateOverrideAsync(new() {
				Context = context,
				OverrideId = new(overrideId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Override = new(
					new(request.NodeId),
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddNodeRateOverrideAsync(
		long userId,
		[FromBody] AddNodeRateOverrideBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.AddNodeRateOverrideAsync(new() {
				Context = context,
				UserId = new(userId),
				Override = new(
					new(request.NodeId),
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/rates", Map(result));
		});
	}

	private static RatesResponse Map(RateSnapshotResult result) =>
		new() { UserCostRates = [.. result.UserCostRates.Select(Map)], NodeRateOverrides = [.. result.NodeRateOverrides.Select(Map)] };

	private static UserCostRateResponse Map(UserCostRateResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			AmountPerHour = result.Rate.Rate.AmountPerHour,
			EffectiveStart = result.Rate.EffectiveStart.ToDateTimeOffset(),
			EffectiveEnd = result.Rate.EffectiveEnd?.ToDateTimeOffset(),
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static NodeRateOverrideResponse Map(NodeRateOverrideResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			NodeId = result.Override.NodeId.Value,
			AmountPerHour = result.Override.Rate.AmountPerHour,
			EffectiveStart = result.Override.EffectiveStart.ToDateTimeOffset(),
			EffectiveEnd = result.Override.EffectiveEnd?.ToDateTimeOffset(),
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	internal sealed class RatesResponse
	{
		public required UserCostRateResponse[] UserCostRates { get; init; }

		public required NodeRateOverrideResponse[] NodeRateOverrides { get; init; }
	}

	internal sealed class UserCostRateResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class NodeRateOverrideResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required long NodeId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class AddUserCostRateBody
	{
		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }
	}

	internal sealed class AddNodeRateOverrideBody
	{
		public required long NodeId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }
	}

	internal sealed class CorrectUserCostRateBody
	{
		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class CorrectNodeRateOverrideBody
	{
		public required long NodeId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}
}
