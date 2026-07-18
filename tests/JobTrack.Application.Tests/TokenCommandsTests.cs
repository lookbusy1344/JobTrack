namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using NodaTime;

public sealed class TokenCommandsTests
{
	private static readonly AppUserId AdministratorId = new(1);
	private static readonly AppUserId WorkerId = new(2);
	private static readonly AppUserId OtherWorkerId = new(3);

	private static FakePersonalAccessTokenPort CreateSeededPort()
	{
		var port = new FakePersonalAccessTokenPort();
		port.SeedRoles(AdministratorId, EmployeeRole.Administrator);
		port.SeedRoles(WorkerId, EmployeeRole.Worker);
		port.SeedRoles(OtherWorkerId, EmployeeRole.Worker);

		return port;
	}

	private static TokenCommands CreateSut(FakePersonalAccessTokenPort port) => new(port);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static Instant OneYearFromNow() => SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(30);

	[Fact]
	public async Task A_worker_can_issue_a_token_for_themselves()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			Label = "my-cli",
			ExpiresAt = OneYearFromNow(),
		});

		result.Token.Should().NotBeNullOrWhiteSpace();
		result.Label.Should().Be("my-cli");
	}

	[Fact]
	public async Task A_worker_cannot_issue_a_token_for_another_user()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = OtherWorkerId,
			Label = "someone-elses-cli",
			ExpiresAt = OneYearFromNow(),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Issuing_a_token_with_an_expiry_in_the_past_throws_invariant_violation()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			Label = "already-expired",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() - Duration.FromSeconds(1),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("personal-access-token-expiry-not-in-future");
	}

	[Fact]
	public async Task Issuing_a_token_beyond_the_maximum_lifetime_throws_invariant_violation()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			Label = "too-long-lived",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(366),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("personal-access-token-expiry-too-long");
	}

	[Fact]
	public async Task A_freshly_issued_token_authenticates_as_its_owner()
	{
		var sut = CreateSut(CreateSeededPort());
		var issued = await sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			Label = "my-cli",
			ExpiresAt = OneYearFromNow(),
		});

		var authenticated = await sut.TryAuthenticateAsync(new() { Token = issued.Token });

		authenticated.Should().NotBeNull();
		authenticated!.UserId.Should().Be(WorkerId);
	}

	[Fact]
	public async Task An_unknown_token_fails_to_authenticate_without_throwing()
	{
		var sut = CreateSut(CreateSeededPort());

		var authenticated = await sut.TryAuthenticateAsync(new() { Token = "jtpat_not-a-real-token" });

		authenticated.Should().BeNull();
	}

	[Fact]
	public async Task A_revoked_token_fails_to_authenticate()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);
		var issued = await sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			Label = "my-cli",
			ExpiresAt = OneYearFromNow(),
		});

		await sut.RevokeAsync(new() { Context = ContextFor(WorkerId), TargetUserId = WorkerId, TokenId = issued.Id });
		var authenticated = await sut.TryAuthenticateAsync(new() { Token = issued.Token });

		authenticated.Should().BeNull();
	}

	[Fact]
	public async Task A_disabled_owner_causes_their_tokens_to_fail_authentication()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);
		var issued = await sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			Label = "my-cli",
			ExpiresAt = OneYearFromNow(),
		});

		port.SetEnabled(WorkerId, false);
		var authenticated = await sut.TryAuthenticateAsync(new() { Token = issued.Token });

		authenticated.Should().BeNull();
	}

	[Fact]
	public async Task An_administrator_can_revoke_all_of_another_users_tokens()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);
		var issued = await sut.IssueAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			Label = "my-cli",
			ExpiresAt = OneYearFromNow(),
		});

		await sut.RevokeAllAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = WorkerId });
		var authenticated = await sut.TryAuthenticateAsync(new() { Token = issued.Token });

		authenticated.Should().BeNull();
	}

	[Fact]
	public async Task A_worker_cannot_list_another_users_tokens()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.ListAsync(new() { Context = ContextFor(WorkerId), TargetUserId = OtherWorkerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}
}
