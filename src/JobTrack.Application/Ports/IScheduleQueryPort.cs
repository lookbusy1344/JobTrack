namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetScheduleAsync" /> (plan §8.5 slice
///     6). Loads the target employee's schedule versions and exceptions alongside the actor's current
///     roles in one round-trip, the same shape as <see cref="IEmployeeQueryPort" />.
/// </summary>
public interface IScheduleQueryPort
{
	/// <summary>Loads the employee's schedule versions/exceptions and the actor's current roles.</summary>
	/// <exception cref="EntityNotFoundException">The actor or the target employee does not exist.</exception>
	Task<ScheduleQueryResult> GetScheduleAsync(
		AppUserId actorId, AppUserId userId, CancellationToken cancellationToken = default);
}
