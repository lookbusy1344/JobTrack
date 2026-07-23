namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetRatesAsync" /> (plan §8.5 slice 7).
///     Loads the target employee's cost rates and node rate overrides alongside the actor's current
///     roles in one round-trip, the same shape as <see cref="IScheduleQueryPort" />.
/// </summary>
internal interface IRateQueryPort
{
	/// <summary>Loads the employee's cost rates/node rate overrides and the actor's current roles.</summary>
	/// <exception cref="EntityNotFoundException">The actor or the target employee does not exist.</exception>
	Task<RateQueryResult> GetRatesAsync(
		AppUserId actorId, AppUserId userId, CancellationToken cancellationToken = default);
}
