namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Ports;

public sealed class AccountCredentialCommandsTests
{
	private static readonly AppUserId ActorId = new(1);

	[Theory]
	[InlineData("")]
	[InlineData("short-password")]
	[InlineData("fourteen-chars")]
	public async Task ChangeOwnPasswordAsync_rejects_a_new_password_shorter_than_the_minimum(string newPassword)
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest(newPassword));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("account-new-password-policy");
		port.ChangeOwnPasswordCallCount.Should().Be(0);
	}

	[Fact]
	public async Task ChangeOwnPasswordAsync_rejects_a_new_password_longer_than_the_maximum()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);
		var tooLong = new string('a', PasswordPolicy.MaximumLength + 1);

		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest(tooLong));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("account-new-password-policy");
		port.ChangeOwnPasswordCallCount.Should().Be(0);
	}

	[Fact]
	public async Task ChangeOwnPasswordAsync_rejects_a_password_that_is_the_accounts_own_username()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);
		var username = new string('u', PasswordPolicy.MinimumLength);

		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest(username, username));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("account-new-password-policy");
		port.ChangeOwnPasswordCallCount.Should().Be(0);
	}

	[Fact]
	public async Task ChangeOwnPasswordAsync_rejects_a_blocklisted_common_password()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		var act = () => sut.ChangeOwnPasswordAsync(CreateRequest("correcthorsebatterystaple"));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("account-new-password-policy");
		port.ChangeOwnPasswordCallCount.Should().Be(0);
	}

	[Fact]
	public async Task ChangeOwnPasswordAsync_accepts_a_new_password_meeting_the_shared_policy()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		_ = await sut.ChangeOwnPasswordAsync(CreateRequest("a genuinely unusual passphrase 42"));

		port.ChangeOwnPasswordCallCount.Should().Be(1);
	}

	private static ChangeOwnPasswordRequest CreateRequest(string newPassword, string username = "grace.hopper") =>
		new() {
			ActorUserId = ActorId,
			IdentityUserId = 1,
			Username = username,
			CurrentPassword = "the-existing-account-password",
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
			++ChangeOwnPasswordCallCount;
			return Task.FromResult(new ChangeOwnPasswordResult {
				SecurityStamp = "security-stamp",
				ConcurrencyStamp = "concurrency-stamp",
			});
		}
	}
}
