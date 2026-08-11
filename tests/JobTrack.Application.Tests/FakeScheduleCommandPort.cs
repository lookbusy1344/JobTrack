namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Authorization;
using Domain.Intervals;
using Domain.Schedules;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IScheduleCommandPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases"). Simulates the authorization guard and overlap checks a real persistence
///     implementation must enforce inside its own transaction.
/// </summary>
internal sealed class FakeScheduleCommandPort : IScheduleCommandPort
{
	private readonly List<ScheduleExceptionResult> _exceptions = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private readonly HashSet<AppUserId> _users = [];
	private readonly List<ScheduleVersionResult> _versions = [];
	private long _nextExceptionId = 1;
	private long _nextVersionId = 1;

	public Instant NowToReturn { get; set; } = Instant.FromUtc(2026, 1, 1, 0, 0);

	public Task<ScheduleVersionResult> AddScheduleVersionAsync(
		AddScheduleVersionRequest request, CancellationToken cancellationToken = default)
	{
		if (!_users.Contains(request.UserId)) {
			throw new EntityNotFoundException($"Employee {request.UserId} does not exist.");
		}

		AuthorizeOrThrow(request.Context.Actor, request.UserId);

		var overlaps = _versions.Any(version =>
			version.UserId == request.UserId
			&& DateRangesOverlap(
				request.Schedule.EffectiveStart, request.Schedule.EffectiveEnd,
				version.Schedule.EffectiveStart, version.Schedule.EffectiveEnd));
		if (overlaps) {
			throw new InvariantViolationException(
				"schedule-version-overlap", "This schedule version's effective range overlaps another for this employee.");
		}

		var result = new ScheduleVersionResult {
			Id = new(_nextVersionId++),
			UserId = request.UserId,
			Schedule = request.Schedule,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_versions.Add(result);

		return Task.FromResult(result);
	}

	public Task<ScheduleExceptionResult> AddScheduleExceptionAsync(
		AddScheduleExceptionRequest request, CancellationToken cancellationToken = default)
	{
		if (!_users.Contains(request.UserId)) {
			throw new EntityNotFoundException($"Employee {request.UserId} does not exist.");
		}

		AuthorizeOrThrow(request.Context.Actor, request.UserId);

		if (request.Entry.Effect == ScheduleExceptionEffect.AddWorkingTime && request.Entry.RateOverride is not null) {
			var overlapsPriced = _exceptions.Any(exception =>
				exception.UserId == request.UserId
				&& exception.Entry.Effect == ScheduleExceptionEffect.AddWorkingTime
				&& exception.Entry.RateOverride is not null
				&& IntervalsOverlap(request.Entry.Interval, exception.Entry.Interval));
			if (overlapsPriced) {
				throw new InvariantViolationException(
					"schedule-exception-priced-additive-overlap",
					"This priced additive exception overlaps another for this employee.");
			}
		}

		var result = new ScheduleExceptionResult {
			Id = new(_nextExceptionId++),
			UserId = request.UserId,
			Entry = request.Entry,
			Reason = request.Reason,
			CreatedBy = request.Context.Actor,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_exceptions.Add(result);

		return Task.FromResult(result);
	}

	public Task<ScheduleVersionResult> CorrectScheduleVersionAsync(
		CorrectScheduleVersionRequest request, CancellationToken cancellationToken = default)
	{
		var index = _versions.FindIndex(v => v.Id == request.VersionId);
		if (index < 0) {
			throw new EntityNotFoundException($"Schedule version {request.VersionId} does not exist.");
		}

		var existing = _versions[index];
		if (request.UserId is AppUserId expectedUserId && existing.UserId != expectedUserId) {
			throw new EntityNotFoundException($"Schedule row {request.VersionId} does not belong to employee {expectedUserId}.");
		}

		AuthorizeOrThrow(request.Context.Actor, existing.UserId);
		if (existing.Version != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} but the current version is {existing.Version}.");
		}

		var overlaps = _versions.Any(version =>
			version.Id != request.VersionId
			&& version.UserId == existing.UserId
			&& DateRangesOverlap(
				request.Schedule.EffectiveStart, request.Schedule.EffectiveEnd,
				version.Schedule.EffectiveStart, version.Schedule.EffectiveEnd));
		if (overlaps) {
			throw new InvariantViolationException(
				"schedule-version-overlap", "This schedule version's effective range overlaps another for this employee.");
		}

		var result = existing with { Schedule = request.Schedule, ChangedAt = NowToReturn, Version = existing.Version + 1 };
		_versions[index] = result;

		return Task.FromResult(result);
	}

	public Task<ScheduleExceptionResult> CorrectScheduleExceptionAsync(
		CorrectScheduleExceptionRequest request, CancellationToken cancellationToken = default)
	{
		var index = _exceptions.FindIndex(e => e.Id == request.ExceptionId);
		if (index < 0) {
			throw new EntityNotFoundException($"Schedule exception {request.ExceptionId} does not exist.");
		}

		var existing = _exceptions[index];
		if (request.UserId is AppUserId expectedUserId && existing.UserId != expectedUserId) {
			throw new EntityNotFoundException($"Schedule row {request.ExceptionId} does not belong to employee {expectedUserId}.");
		}

		AuthorizeOrThrow(request.Context.Actor, existing.UserId);
		if (existing.Version != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} but the current version is {existing.Version}.");
		}

		if (request.Entry.Effect == ScheduleExceptionEffect.AddWorkingTime && request.Entry.RateOverride is not null) {
			var overlapsPriced = _exceptions.Any(exception =>
				exception.Id != request.ExceptionId
				&& exception.UserId == existing.UserId
				&& exception.Entry.Effect == ScheduleExceptionEffect.AddWorkingTime
				&& exception.Entry.RateOverride is not null
				&& IntervalsOverlap(request.Entry.Interval, exception.Entry.Interval));
			if (overlapsPriced) {
				throw new InvariantViolationException(
					"schedule-exception-priced-additive-overlap",
					"This priced additive exception overlaps another for this employee.");
			}
		}

		var result = existing with {
			Entry = request.Entry,
			Reason = request.Reason,
			ChangedAt = NowToReturn,
			Version = existing.Version + 1,
		};
		_exceptions[index] = result;

		return Task.FromResult(result);
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles)
	{
		_roles[actorId] = [.. roles];
		_users.Add(actorId);
	}

	public void SeedUser(AppUserId userId) => _users.Add(userId);

	private static bool DateRangesOverlap(LocalDate aStart, LocalDate? aEnd, LocalDate bStart, LocalDate? bEnd)
	{
		var aEndValue = aEnd ?? LocalDate.MaxIsoValue;
		var bEndValue = bEnd ?? LocalDate.MaxIsoValue;

		return aStart < bEndValue && bStart < aEndValue;
	}

	private static bool IntervalsOverlap(WorkInterval a, WorkInterval b) => a.Start < b.End && b.Start < a.End;

	private void AuthorizeOrThrow(AppUserId actorId, AppUserId targetUserId)
	{
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];

		if (!ScheduleAccessPolicy.CanManage(roles, actorId == targetUserId)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage schedule data for {targetUserId}.");
		}
	}
}
