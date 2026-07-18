namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetRatesAsync" />. Unlike <see cref="GetScheduleRequest" />,
///     there is no "own rate" self-view carve-out — <see cref="Domain.Authorization.RateAccessPolicy" />'s
///     own documentation notes "a worker never sets their own pay rate", and viewing is gated
///     separately by <see cref="Domain.Authorization.CostAccessPolicy" />.
/// </summary>
public sealed record GetRatesRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose cost rates and node rate overrides are requested.</summary>
	public required AppUserId UserId { get; init; }
}
