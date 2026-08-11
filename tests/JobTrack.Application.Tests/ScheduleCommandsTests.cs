namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Schedules;
using NodaTime;

public sealed class ScheduleCommandsTests
{
	private static readonly AppUserId AdministratorId = new(1);
	private static readonly AppUserId WorkerId = new(2);
	private static readonly AppUserId OtherWorkerId = new(3);

	private static FakeScheduleCommandPort CreateSeededPort()
	{
		var port = new FakeScheduleCommandPort();
		port.SeedRoles(AdministratorId, EmployeeRole.Administrator);
		port.SeedRoles(WorkerId, EmployeeRole.Worker);
		port.SeedRoles(OtherWorkerId, EmployeeRole.Worker);

		return port;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static ScheduleVersion CreateWeekdayScheduleVersion(LocalDate effectiveStart, LocalDate? effectiveEnd = null) =>
		new(
			DateTimeZoneProviders.Tzdb["Europe/London"],
			effectiveStart,
			effectiveEnd,
			[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]);

	[Fact]
	public async Task A_worker_can_add_their_own_schedule_version()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var result = await sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		result.UserId.Should().Be(WorkerId);
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task An_administrator_can_add_a_schedule_version_for_another_employee()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var result = await sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(AdministratorId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		result.UserId.Should().Be(WorkerId);
	}

	[Fact]
	public async Task A_worker_cannot_add_a_schedule_version_for_another_employee()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var act = () => sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(OtherWorkerId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_schedule_version_for_a_nonexistent_employee_throws_not_found()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var act = () => sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(AdministratorId),
			UserId = new(999),
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Overlapping_schedule_versions_for_the_same_employee_throw_an_invariant_violation()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		await sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1), new LocalDate(2026, 6, 1)),
		});

		var act = () => sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 3, 1)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("schedule-version-overlap");
	}

	[Fact]
	public async Task A_worker_can_add_their_own_schedule_exception()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var entry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.RemoveWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
			null);

		var result = await sut.AddScheduleExceptionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Entry = entry,
			Reason = "Public holiday",
		});

		result.UserId.Should().Be(WorkerId);
		result.Entry.Effect.Should().Be(ScheduleExceptionEffect.RemoveWorkingTime);
	}

	[Fact]
	public async Task A_worker_cannot_add_a_schedule_exception_for_another_employee()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var entry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.RemoveWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
			null);

		var act = () => sut.AddScheduleExceptionAsync(new() {
			Context = ContextFor(OtherWorkerId),
			UserId = WorkerId,
			Entry = entry,
			Reason = "Public holiday",
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Overlapping_priced_additive_exceptions_for_the_same_employee_throw_an_invariant_violation()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var firstEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 18, 0), Instant.FromUtc(2026, 1, 1, 22, 0)),
			new HourlyRate(30m));
		await sut.AddScheduleExceptionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Entry = firstEntry,
			Reason = "Overtime shift",
		});
		var overlappingEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 20, 0), Instant.FromUtc(2026, 1, 1, 23, 0)),
			new HourlyRate(35m));

		var act = () => sut.AddScheduleExceptionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Entry = overlappingEntry,
			Reason = "Second overtime shift",
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("schedule-exception-priced-additive-overlap");
	}

	[Fact]
	public async Task Overlapping_unpriced_additive_exceptions_are_allowed()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var firstEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 18, 0), Instant.FromUtc(2026, 1, 1, 22, 0)),
			null);
		await sut.AddScheduleExceptionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Entry = firstEntry,
			Reason = "Overtime shift",
		});
		var overlappingEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 20, 0), Instant.FromUtc(2026, 1, 1, 23, 0)),
			null);

		var result = await sut.AddScheduleExceptionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Entry = overlappingEntry,
			Reason = "Second overtime shift",
		});

		result.Entry.Should().Be(overlappingEntry);
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_version()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var added = await sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		var result = await sut.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			VersionId = added.Id,
			UserId = WorkerId,
			Version = added.Version,
			Reason = "Fixed a typo in the start date",
			Schedule = CreateWeekdayScheduleVersion(new(2026, 2, 1)),
		});

		result.Schedule.EffectiveStart.Should().Be(new(2026, 2, 1));
		result.Version.Should().Be(added.Version + 1);
	}

	[Fact]
	public async Task A_worker_cannot_correct_another_employees_schedule_version()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var added = await sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		var act = () => sut.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(OtherWorkerId),
			VersionId = added.Id,
			UserId = WorkerId,
			Version = added.Version,
			Reason = "Attempted correction",
			Schedule = CreateWeekdayScheduleVersion(new(2026, 2, 1)),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Correcting_a_schedule_version_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var added = await sut.AddScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		var act = () => sut.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(WorkerId),
			VersionId = added.Id,
			UserId = WorkerId,
			Version = added.Version + 1,
			Reason = "Fixed a typo in the start date",
			Schedule = CreateWeekdayScheduleVersion(new(2026, 2, 1)),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_exception()
	{
		var sut = new ScheduleCommands(CreateSeededPort());
		var added = await sut.AddScheduleExceptionAsync(new() {
			Context = ContextFor(WorkerId),
			UserId = WorkerId,
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
				null),
			Reason = "Public holiday",
		});
		var correctedEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.RemoveWorkingTime,
			new(Instant.FromUtc(2026, 1, 3, 0, 0), Instant.FromUtc(2026, 1, 4, 0, 0)),
			null);

		var result = await sut.CorrectScheduleExceptionAsync(new() {
			Context = ContextFor(WorkerId),
			ExceptionId = added.Id,
			UserId = WorkerId,
			Version = added.Version,
			Reason = "Wrong date entered originally",
			Entry = correctedEntry,
		});

		result.Entry.Should().Be(correctedEntry);
		result.Reason.Should().Be("Wrong date entered originally");
	}

	[Fact]
	public void Constructor_rejects_a_null_port()
	{
		var act = () => new ScheduleCommands(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task AddScheduleVersionAsync_rejects_a_null_request()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var act = () => sut.AddScheduleVersionAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task AddScheduleExceptionAsync_rejects_a_null_request()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var act = () => sut.AddScheduleExceptionAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task CorrectScheduleVersionAsync_rejects_a_null_request()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var act = () => sut.CorrectScheduleVersionAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task CorrectScheduleExceptionAsync_rejects_a_null_request()
	{
		var sut = new ScheduleCommands(CreateSeededPort());

		var act = () => sut.CorrectScheduleExceptionAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
