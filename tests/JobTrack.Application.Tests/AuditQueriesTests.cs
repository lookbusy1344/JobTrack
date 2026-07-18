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

		results.Should().ContainSingle();
		results[0].EntityType.Should().Be("user_cost_rate");
		results[0].Operation.Should().Be("add-user-cost-rate");
		results[0].IsRedacted.Should().BeTrue();
		results[0].AfterData.Should().BeNull();
	}

	[Fact]
	public async Task An_auditor_with_cost_visibility_sees_a_rate_events_full_payload()
	{
		var sut = new AuditQueries(CreateSeededPort());

		var results = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(CostViewerAuditorId),
			Filter = new() { EntityType = "user_cost_rate" },
		});

		results.Should().ContainSingle();
		results[0].IsRedacted.Should().BeFalse();
		results[0].AfterData!.Value.Should().ContainKey("amount_per_hour");
	}

	[Fact]
	public async Task A_non_sensitive_event_is_never_redacted_even_without_cost_visibility()
	{
		var sut = new AuditQueries(CreateSeededPort());

		var results = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(AuditorId), Filter = new() { EntityType = "job_node" } });

		results.Should().ContainSingle();
		results[0].IsRedacted.Should().BeFalse();
		results[0].AfterData!.Value.Should().ContainKey("description");
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

		results.Should().HaveCount(2);
		results[0].OccurredAt.Should().BeGreaterThan(results[1].OccurredAt);
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
}
