namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using NodaTime;
using Ports;

public sealed class AuditQueriesTests
{
	private static readonly AppUserId AuditorId = new(1);
	private static readonly AppUserId CostViewerAuditorId = new(2);
	private static readonly AppUserId WorkerId = new(3);
	private static readonly AppUserId ActorId = new(9);

	private static Instant At(int hour) => Instant.FromUtc(2026, 1, 1, hour, 0);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static AuditEventRecord JobNodeEvent() => new() {
		Id = new(1),
		OccurredAt = At(9),
		ActorId = ActorId,
		Operation = "create-job-node",
		EntityType = "job_node",
		EntityId = 42,
		CorrelationId = Guid.NewGuid(),
		Reason = null,
		BeforeData = null,
		AfterData = EquatableDictionaryFactory.CopyOf(new Dictionary<string, string?> { ["description"] = "Do the thing" }),
		IsSensitive = false,
	};

	private static AuditEventRecord RateEvent() => new() {
		Id = new(2),
		OccurredAt = At(10),
		ActorId = ActorId,
		Operation = "add-user-cost-rate",
		EntityType = "user_cost_rate",
		EntityId = 7,
		CorrelationId = Guid.NewGuid(),
		Reason = null,
		BeforeData = null,
		AfterData = EquatableDictionaryFactory.CopyOf(new Dictionary<string, string?> { ["amount_per_hour"] = "60.00" }),
		IsSensitive = true,
	};

	private static AuditEventRecord Event(long id, Instant occurredAt) => new() {
		Id = new(id),
		OccurredAt = occurredAt,
		ActorId = ActorId,
		Operation = "create-job-node",
		EntityType = "job_node",
		EntityId = id,
		CorrelationId = Guid.NewGuid(),
		Reason = null,
		BeforeData = null,
		AfterData = null,
		IsSensitive = false,
	};

	private static FakeAuditQueryPort CreateSeededPort()
	{
		var port = new FakeAuditQueryPort();
		port.SeedRoles(AuditorId, EmployeeRole.Auditor);
		port.SeedRoles(CostViewerAuditorId, EmployeeRole.Auditor, EmployeeRole.CostViewer);
		port.SeedRoles(WorkerId, EmployeeRole.Worker);
		port.SeedEvent(JobNodeEvent());
		port.SeedEvent(RateEvent());

		return port;
	}

	[Fact]
	public async Task An_auditor_without_cost_visibility_sees_a_rate_events_metadata_but_not_its_payload()
	{
		var sut = new AuditQueries(CreateSeededPort());

		var results = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new() { EntityType = "user_cost_rate" } });

		results.Events.Should().ContainSingle();
		results.Events[0].EntityType.Should().Be("user_cost_rate");
		results.Events[0].Operation.Should().Be("add-user-cost-rate");
		results.Events[0].IsRedacted.Should().BeTrue();
		results.Events[0].AfterData.Should().BeNull();
	}

	[Fact]
	public async Task An_auditor_with_cost_visibility_sees_a_rate_events_full_payload()
	{
		var sut = new AuditQueries(CreateSeededPort());

		var results = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(CostViewerAuditorId),
			Filter = new() { EntityType = "user_cost_rate" },
		});

		results.Events.Should().ContainSingle();
		results.Events[0].IsRedacted.Should().BeFalse();
		results.Events[0].AfterData!.Value.Should().ContainKey("amount_per_hour");
	}

	[Fact]
	public async Task A_non_sensitive_event_is_never_redacted_even_without_cost_visibility()
	{
		var sut = new AuditQueries(CreateSeededPort());

		var results = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new() { EntityType = "job_node" } });

		results.Events.Should().ContainSingle();
		results.Events[0].IsRedacted.Should().BeFalse();
		results.Events[0].AfterData!.Value.Should().ContainKey("description");
	}

	[Fact]
	public async Task A_worker_without_audit_permission_cannot_search_audit_history()
	{
		var port = CreateSeededPort();
		var sut = new AuditQueries(port);

		var act = () => sut.SearchAuditEventsAsync(new() { Context = ContextFor(WorkerId), Filter = new() });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		port.GetActorRolesCallCount.Should().Be(1);
		port.SearchAuditEventsCallCount.Should().Be(0);
	}

	[Fact]
	public async Task Results_are_ordered_most_recent_first()
	{
		var sut = new AuditQueries(CreateSeededPort());

		var results = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new() });

		results.Events.Should().HaveCount(2);
		results.Events[0].OccurredAt.Should().BeGreaterThan(results.Events[1].OccurredAt);
	}

	[Fact]
	public void Constructor_rejects_a_null_port()
	{
		var act = () => new AuditQueries(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task SearchAuditEventsAsync_rejects_a_null_request()
	{
		var sut = new AuditQueries(CreateSeededPort());

		Func<Task> act = () => sut.SearchAuditEventsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public void SearchAuditEventsAsync_throws_synchronously_for_a_null_request()
	{
		var sut = new AuditQueries(CreateSeededPort());

		Action act = () => _ = sut.SearchAuditEventsAsync(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task An_absent_page_size_asks_the_port_for_the_default_page_size_plus_one_probe_row()
	{
		var port = CreateSeededPort();
		var sut = new AuditQueries(port);

		_ = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new() });

		port.ObservedLimits.Should().ContainSingle().Which.Should().Be(AuditSearchPaging.DefaultPageSize + 1);
	}

	[Fact]
	public async Task A_requested_page_size_beyond_the_maximum_is_clamped_before_reaching_the_port()
	{
		var port = CreateSeededPort();
		var sut = new AuditQueries(port);

		_ = await sut.SearchAuditEventsAsync(
			new() { Context = ContextFor(AuditorId), Filter = new(), PageSize = AuditSearchPaging.MaxPageSize + 500 });

		port.ObservedLimits.Should().ContainSingle().Which.Should().Be(AuditSearchPaging.MaxPageSize + 1);
	}

	[Fact]
	public async Task A_page_that_exactly_fills_the_requested_size_still_returns_a_continuation_cursor_when_a_probe_row_exists()
	{
		var port = new FakeAuditQueryPort();
		port.SeedRoles(AuditorId, EmployeeRole.Auditor);
		port.SeedEvent(Event(1, At(1)));
		port.SeedEvent(Event(2, At(2)));
		port.SeedEvent(Event(3, At(3)));
		var sut = new AuditQueries(port);

		var page = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new(), PageSize = 2 });

		page.Events.Should().HaveCount(2);
		page.Events[0].Id.Should().Be(new(3));
		page.Events[1].Id.Should().Be(new(2));
		page.ContinuationCursor.Should().NotBeNull();
	}

	[Fact]
	public async Task The_last_page_carries_no_continuation_cursor()
	{
		var port = new FakeAuditQueryPort();
		port.SeedRoles(AuditorId, EmployeeRole.Auditor);
		port.SeedEvent(Event(1, At(1)));
		port.SeedEvent(Event(2, At(2)));
		var sut = new AuditQueries(port);

		var page = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new(), PageSize = 5 });

		page.Events.Should().HaveCount(2);
		page.ContinuationCursor.Should().BeNull();
	}

	[Fact]
	public async Task Passing_the_first_pages_continuation_cursor_back_fetches_the_next_non_overlapping_page()
	{
		var port = new FakeAuditQueryPort();
		port.SeedRoles(AuditorId, EmployeeRole.Auditor);
		port.SeedEvent(Event(1, At(1)));
		port.SeedEvent(Event(2, At(2)));
		port.SeedEvent(Event(3, At(3)));
		var sut = new AuditQueries(port);

		var firstPage = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new(), PageSize = 2 });
		var secondPage = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(AuditorId),
			Filter = new(),
			PageSize = 2,
			Cursor = firstPage.ContinuationCursor,
		});

		secondPage.Events.Should().ContainSingle();
		secondPage.Events[0].Id.Should().Be(new(1));
		secondPage.ContinuationCursor.Should().BeNull();
	}

	[Fact]
	public async Task Equal_timestamp_events_are_tie_broken_by_id_and_paged_without_overlap_or_gaps()
	{
		var tied = At(5);
		var port = new FakeAuditQueryPort();
		port.SeedRoles(AuditorId, EmployeeRole.Auditor);
		port.SeedEvent(Event(1, tied));
		port.SeedEvent(Event(2, tied));
		port.SeedEvent(Event(3, tied));
		var sut = new AuditQueries(port);

		var firstPage = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new(), PageSize = 2 });
		var secondPage = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(AuditorId),
			Filter = new(),
			PageSize = 2,
			Cursor = firstPage.ContinuationCursor,
		});

		firstPage.Events.Select(e => e.Id).Should().Equal(new AuditEventId(3), new AuditEventId(2));
		secondPage.Events.Select(e => e.Id).Should().Equal(new AuditEventId(1));
	}

	[Fact]
	public async Task A_malformed_cursor_is_rejected_without_reaching_the_port()
	{
		var port = CreateSeededPort();
		var sut = new AuditQueries(port);

		var act = () => sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new(), Cursor = "not-a-valid-cursor!!" });

		await act.Should().ThrowAsync<ArgumentException>();
		port.SearchAuditEventsCallCount.Should().Be(0);
	}
}
