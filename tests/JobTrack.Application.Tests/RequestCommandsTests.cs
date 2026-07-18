namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;

public sealed class RequestCommandsTests
{
	private static readonly AppUserId RequesterId = new(10);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	[Fact]
	public async Task SubmitAsync_delegates_to_the_port()
	{
		var port = new FakeJobRequestCommandPort();
		var sut = new RequestCommands(port);

		var result = await sut.SubmitAsync(new() {
			Context = ContextFor(RequesterId),
			HoldingAreaId = new(1),
			Description = "Please schedule this work.",
		});

		port.LastSubmitRequest.Should().NotBeNull();
		port.LastSubmitRequest!.Context.Actor.Should().Be(RequesterId);
		result.Description.Should().Be("Please schedule this work.");
	}

	[Fact]
	public async Task SubmitAsync_rejects_a_null_request()
	{
		var sut = new RequestCommands(new FakeJobRequestCommandPort());

		var act = () => sut.SubmitAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	/// <summary>
	///     Staff triage moves a requester job to a new parent (plan §5, §9 Stage 5) without
	///     altering its <c>job_request</c> anchor — the anchor itself is preserved by the persistence
	///     port keying on <see cref="JobNodeId" />, not the parent (TC-DB-REQ-003/-005); this pins the
	///     facade passes the move through unchanged.
	/// </summary>
	[Fact]
	public async Task MoveAsync_delegates_to_the_port_unchanged()
	{
		var jobManagerId = new AppUserId(20);
		var port = new FakeJobRequestCommandPort();
		var sut = new RequestCommands(port);
		var nodeId = new JobNodeId(100);
		var newParentId = new JobNodeId(200);

		var result = await sut.MoveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = nodeId,
			NewParentId = newParentId,
			Version = 1,
		});

		port.LastMoveRequest.Should().NotBeNull();
		port.LastMoveRequest!.NodeId.Should().Be(nodeId);
		port.LastMoveRequest!.NewParentId.Should().Be(newParentId);
		result.Id.Should().Be(nodeId);
		result.ParentId.Should().Be(newParentId);
	}

	[Fact]
	public async Task MoveAsync_rejects_a_null_request()
	{
		var sut = new RequestCommands(new FakeJobRequestCommandPort());

		var act = () => sut.MoveAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
