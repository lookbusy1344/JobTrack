namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Schedule commands (plan §7.3 step 8: add schedule versions and exceptions;
///     docs/api/jobtrack-client-design.md).
/// </summary>
public interface IScheduleCommands
{
	/// <summary>Adds an effective-dated schedule version (spec §8.1).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this employee's schedule (see <see cref="Domain.Authorization.ScheduleAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The employee does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     This version's effective range overlaps another of this employee's schedule versions
	///     (<c>ConstraintId</c> <c>"schedule-version-overlap"</c>, spec §8.1).
	/// </exception>
	Task<ScheduleVersionResult> AddScheduleVersionAsync(
		AddScheduleVersionRequest request, CancellationToken cancellationToken = default);

	/// <summary>Adds an additive or subtractive schedule exception (spec §8.3).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this employee's schedule (see <see cref="Domain.Authorization.ScheduleAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The employee does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     This is a priced <c>AddWorkingTime</c> exception overlapping another priced additive
	///     exception for this employee (<c>ConstraintId</c> <c>"schedule-exception-priced-additive-overlap"</c>,
	///     spec §8.3).
	/// </exception>
	Task<ScheduleExceptionResult> AddScheduleExceptionAsync(
		AddScheduleExceptionRequest request, CancellationToken cancellationToken = default);

	/// <summary>Corrects a historical schedule version in place (ADR 0003).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this employee's schedule (see <see cref="Domain.Authorization.ScheduleAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">
	///     The schedule version does not exist, or does not belong to the given <c>UserId</c>.
	/// </exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version does not match the schedule version's current version.</exception>
	/// <exception cref="InvariantViolationException">
	///     The corrected effective range overlaps another of this employee's schedule versions
	///     (<c>ConstraintId</c> <c>"schedule-version-overlap"</c>, spec §8.1).
	/// </exception>
	Task<ScheduleVersionResult> CorrectScheduleVersionAsync(
		CorrectScheduleVersionRequest request, CancellationToken cancellationToken = default);

	/// <summary>Corrects a historical schedule exception in place (ADR 0003).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this employee's schedule (see <see cref="Domain.Authorization.ScheduleAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">
	///     The schedule exception does not exist, or does not belong to the given <c>UserId</c>.
	/// </exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version does not match the schedule exception's current version.</exception>
	/// <exception cref="InvariantViolationException">
	///     The corrected exception is a priced <c>AddWorkingTime</c> exception overlapping another priced
	///     additive exception for this employee (<c>ConstraintId</c> <c>"schedule-exception-priced-additive-overlap"</c>,
	///     spec §8.3).
	/// </exception>
	Task<ScheduleExceptionResult> CorrectScheduleExceptionAsync(
		CorrectScheduleExceptionRequest request, CancellationToken cancellationToken = default);
}
