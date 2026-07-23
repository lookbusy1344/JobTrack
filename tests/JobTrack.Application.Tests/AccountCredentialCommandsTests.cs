namespace JobTrack.Application.Tests;

using Abstractions;
using Application.Ports;
using AwesomeAssertions;

public sealed class AccountCredentialCommandsTests
{
	private static readonly AppUserId ActorId = new(1);

	[Theory]
	[InlineData("abc12")]
	[InlineData("123456")]
	[InlineData("abcdef")]
	public async Task ChangeOwnPasswordAsync_rejects_a_new_password_outside_the_shared_policy(string newPassword)
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest(newPassword));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("account-new-password-policy");
		port.ChangeOwnPasswordCallCount.Should().Be(0);
	}

	[Fact]
	public async Task ChangeOwnPasswordAsync_accepts_a_new_password_meeting_the_shared_policy()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		_ = await sut.ChangeOwnPasswordAsync(CreateRequest("abc123"));

		port.ChangeOwnPasswordCallCount.Should().Be(1);
	}

	private static ChangeOwnPasswordRequest CreateRequest(string newPassword) =>
		new() {
			ActorUserId = ActorId,
			IdentityUserId = 1,
			CurrentPassword = "old-password-1",
			NewPassword = newPassword,
			CorrelationId = Guid.NewGuid(),
		};

	private sealed class FakeAccountCredentialPort : IAccountCredentialPort
	{
		public int ChangeOwnPasswordCallCount { get; private set; }

		public Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
			SetTwoFactorStateRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(
			ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default)
		{
			ChangeOwnPasswordCallCount++;
			return Task.FromResult(new ChangeOwnPasswordResult {
				SecurityStamp = "security-stamp",
				ConcurrencyStamp = "concurrency-stamp",
			});
		}
	}
}
