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

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task AddPasskeyAsync_rejects_a_blank_name(string name)
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		var act = () => sut.AddPasskeyAsync(CreateAddRequest(name));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("passkey-name-policy");
		port.AddPasskeyCallCount.Should().Be(0);
	}

	[Fact]
	public async Task AddPasskeyAsync_rejects_a_name_longer_than_the_maximum()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);
		var tooLong = new string('a', PasskeyPolicy.MaximumNameLength + 1);

		var act = () => sut.AddPasskeyAsync(CreateAddRequest(tooLong));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("passkey-name-policy");
		port.AddPasskeyCallCount.Should().Be(0);
	}

	[Fact]
	public async Task AddPasskeyAsync_rejects_a_credential_that_is_not_user_verified()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		var act = () => sut.AddPasskeyAsync(CreateAddRequest("Work MacBook", false));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("passkey-not-user-verified");
		port.AddPasskeyCallCount.Should().Be(0);
	}

	[Fact]
	public async Task AddPasskeyAsync_rejects_oversized_credential_material_before_calling_the_port()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);
		var request = CreateAddRequest("Work MacBook");
		request = new() {
			ActorUserId = request.ActorUserId,
			IdentityUserId = request.IdentityUserId,
			Name = request.Name,
			Credential = new() {
				CredentialId = new byte[PasskeyPolicy.MaximumCredentialIdByteLength + 1],
				PublicKey = request.Credential.PublicKey,
				SignCount = request.Credential.SignCount,
				Transports = request.Credential.Transports,
				IsUserVerified = request.Credential.IsUserVerified,
				IsBackupEligible = request.Credential.IsBackupEligible,
				IsBackedUp = request.Credential.IsBackedUp,
				AttestationObject = request.Credential.AttestationObject,
				ClientDataJson = request.Credential.ClientDataJson,
			},
			CorrelationId = request.CorrelationId,
		};

		var act = () => sut.AddPasskeyAsync(request);

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("passkey-credential-material-policy");
		port.AddPasskeyCallCount.Should().Be(0);
	}

	[Fact]
	public async Task AddPasskeyAsync_forwards_an_acceptable_request_to_the_port()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);

		_ = await sut.AddPasskeyAsync(CreateAddRequest("Work MacBook"));

		port.AddPasskeyCallCount.Should().Be(1);
	}

	[Fact]
	public async Task RenamePasskeyAsync_rejects_a_name_over_the_maximum()
	{
		var port = new FakeAccountCredentialPort();
		var sut = new AccountCredentialCommands(port);
		var request = new RenamePasskeyRequest {
			ActorUserId = ActorId,
			IdentityUserId = 1,
			CredentialId = "AQID",
			NewName = new('a', PasskeyPolicy.MaximumNameLength + 1),
			CorrelationId = Guid.NewGuid(),
		};

		var act = () => sut.RenamePasskeyAsync(request);

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("passkey-name-policy");
		port.RenamePasskeyCallCount.Should().Be(0);
	}

	private static AddPasskeyRequest CreateAddRequest(string name, bool isUserVerified = true) =>
		new() {
			ActorUserId = ActorId,
			IdentityUserId = 1,
			Name = name,
			Credential = new() {
				CredentialId = new byte[] {
					1, 2, 3,
				},
				PublicKey = new byte[] {
					4, 5, 6,
				},
				SignCount = 0,
				IsUserVerified = isUserVerified,
				AttestationObject = new byte[] {
					7,
				},
				ClientDataJson = new byte[] {
					8,
				},
			},
			CorrelationId = Guid.NewGuid(),
		};

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

		public int AddPasskeyCallCount { get; private set; }

		public int RenamePasskeyCallCount { get; private set; }

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

		public Task<EnsurePasskeyUserHandleResult> EnsurePasskeyUserHandleAsync(
			EnsurePasskeyUserHandleRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new EnsurePasskeyUserHandleResult {
				UserHandle = "handle",
			});

		public Task<AddPasskeyResult> AddPasskeyAsync(
			AddPasskeyRequest request, CancellationToken cancellationToken = default)
		{
			++AddPasskeyCallCount;
			return Task.FromResult(new AddPasskeyResult {
				SecurityStamp = "security-stamp",
				ConcurrencyStamp = "concurrency-stamp",
				CredentialId = "credential-id",
			});
		}

		public Task<IReadOnlyList<PasskeySummary>> ListPasskeysAsync(
			ListPasskeysRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<PasskeySummary>>([]);

		public Task<RenamePasskeyResult> RenamePasskeyAsync(
			RenamePasskeyRequest request, CancellationToken cancellationToken = default)
		{
			++RenamePasskeyCallCount;
			return Task.FromResult(new RenamePasskeyResult {
				Passkey = new() {
					CredentialId = request.CredentialId,
					Name = request.NewName,
					CreatedAt = DateTimeOffset.UnixEpoch,
				},
			});
		}

		public Task<RemovePasskeyResult> RemovePasskeyAsync(
			RemovePasskeyRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new RemovePasskeyResult {
				SecurityStamp = "security-stamp",
				ConcurrencyStamp = "concurrency-stamp",
			});
	}
}
