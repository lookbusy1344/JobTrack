namespace JobTrack.Application.Ports;

/// <summary>
///     The persistence-owned port backing <see cref="IScheduleCommands" /> (plan §7.3 step 8). Each
///     method is one atomic transaction: the implementation reloads the actor's current roles and
///     whether the schedule is their own, and applies <see cref="Domain.Authorization.ScheduleAccessPolicy" />
///     itself before writing — the same mutation-safety shape as the other command ports.
/// </summary>
public interface IScheduleCommandPort
{
	/// <inheritdoc cref="IScheduleCommands.AddScheduleVersionAsync" />
	Task<ScheduleVersionResult> AddScheduleVersionAsync(
		AddScheduleVersionRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IScheduleCommands.AddScheduleExceptionAsync" />
	Task<ScheduleExceptionResult> AddScheduleExceptionAsync(
		AddScheduleExceptionRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IScheduleCommands.CorrectScheduleVersionAsync" />
	Task<ScheduleVersionResult> CorrectScheduleVersionAsync(
		CorrectScheduleVersionRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IScheduleCommands.CorrectScheduleExceptionAsync" />
	Task<ScheduleExceptionResult> CorrectScheduleExceptionAsync(
		CorrectScheduleExceptionRequest request, CancellationToken cancellationToken = default);
}
