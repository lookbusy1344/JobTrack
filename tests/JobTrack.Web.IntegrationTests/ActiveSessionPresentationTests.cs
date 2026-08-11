namespace JobTrack.Web.IntegrationTests;

using Abstractions;
using Application;
using AwesomeAssertions;
using NodaTime;

/// <summary>
///     Pure unit tests for <see cref="ActiveSessionPresentation" /> and <see cref="ActiveSessionGrouping" />
///     (ADR 0044/Stage 4 of the browse-sessions plan): the collection projection never drops a session,
///     and the derivation never picks a "representative" without the caller asking for one. At least
///     three simultaneous workers, per the plan's test-matrix note that a two-row fixture can still
///     accidentally pass code that treats one session as primary.
/// </summary>
public sealed class ActiveSessionPresentationTests
{
	private static readonly JobNodeId Leaf = new(100);
	private static readonly AppUserId Viewer = new(1);
	private static readonly AppUserId Alice = new(2);
	private static readonly AppUserId Bob = new(3);

	[Fact]
	public void Grouping_never_drops_a_session_with_three_simultaneous_workers()
	{
		var sessions = new[] {
			Session(1, Viewer, Instant.FromUtc(2026, 1, 1, 9, 0)), Session(2, Alice, Instant.FromUtc(2026, 1, 1, 9, 5)),
			Session(3, Bob, Instant.FromUtc(2026, 1, 1, 9, 10)),
		};

		var grouped = ActiveSessionGrouping.Group(sessions);

		grouped[Leaf].Should().HaveCount(3);
		grouped[Leaf].Select(s => s.WorkedByUserId).Should().BeEquivalentTo([Viewer, Alice, Bob]);
	}

	[Fact]
	public void Grouping_orders_sessions_by_started_at_then_id()
	{
		var sessions = new[] {
			Session(3, Bob, Instant.FromUtc(2026, 1, 1, 9, 10)), Session(1, Viewer, Instant.FromUtc(2026, 1, 1, 9, 0)),
			Session(2, Alice, Instant.FromUtc(2026, 1, 1, 9, 0)),
		};

		var grouped = ActiveSessionGrouping.Group(sessions);

		grouped[Leaf].Select(s => s.Id.Value).Should().ContainInOrder(1, 2, 3);
	}

	[Fact]
	public void Derive_separates_the_viewers_own_session_from_every_other_worker()
	{
		var sessions = new[] {
			Session(1, Viewer, Instant.FromUtc(2026, 1, 1, 9, 0)), Session(2, Alice, Instant.FromUtc(2026, 1, 1, 9, 5)),
			Session(3, Bob, Instant.FromUtc(2026, 1, 1, 9, 10)),
		};

		var presentation = ActiveSessionPresentation.Derive(sessions, Viewer);

		presentation.ViewerSession.Should().NotBeNull();
		presentation.ViewerSession!.WorkedByUserId.Should().Be(Viewer);
		presentation.OtherSessions.Should().HaveCount(2);
		presentation.OtherSessions.Select(s => s.WorkedByUserId).Should().BeEquivalentTo([Alice, Bob]);
		presentation.Count.Should().Be(3);
	}

	[Fact]
	public void Derive_reports_no_viewer_session_when_the_viewer_is_not_among_the_active_workers()
	{
		var sessions = new[] { Session(2, Alice, Instant.FromUtc(2026, 1, 1, 9, 5)), Session(3, Bob, Instant.FromUtc(2026, 1, 1, 9, 10)) };

		var presentation = ActiveSessionPresentation.Derive(sessions, Viewer);

		presentation.ViewerSession.Should().BeNull();
		presentation.OtherSessions.Should().HaveCount(2);
		presentation.Count.Should().Be(2);
	}

	[Fact]
	public void Derive_reports_the_full_stable_order_regardless_of_viewer()
	{
		var sessions = new[] {
			Session(3, Bob, Instant.FromUtc(2026, 1, 1, 9, 10)), Session(1, Viewer, Instant.FromUtc(2026, 1, 1, 9, 0)),
			Session(2, Alice, Instant.FromUtc(2026, 1, 1, 9, 5)),
		};

		var presentation = ActiveSessionPresentation.Derive(sessions, Viewer);

		presentation.StableOrder.Select(s => s.Id.Value).Should().ContainInOrder(1, 2, 3);
	}

	private static WorkSessionResult Session(long id, AppUserId workedByUserId, Instant startedAt) => new() {
		Id = new(id),
		LeafWorkId = Leaf,
		WorkedByUserId = workedByUserId,
		StartedAt = startedAt,
		ChangedAt = startedAt,
		Version = 1,
	};
}
