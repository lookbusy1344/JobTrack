namespace JobTrack.Persistence.Shared.Ports;

using System.Buffers.Text;
using Abstractions;
using Application;
using Application.Ports;
using Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;

internal sealed class AccountCredentialPort(
	IProviderWriteOperations provider,
	IClock clock,
	IPasswordHasher<EmployeeCredentialSubject> passwordHasher) : IAccountCredentialPort
{
	private static readonly EmployeeCredentialSubject CredentialSubject = new();

	public async Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
		SetTwoFactorStateRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);
		_ = await IdentityUserWriteLock.AcquireAsync(context, provider, request.ActorUserId, cancellationToken).ConfigureAwait(false);

		var identityUser = await context.Set<IdentityUserEntity>()
										.FirstOrDefaultAsync(user => user.Id == request.IdentityUserId, cancellationToken).ConfigureAwait(false)
						   ?? throw new EntityNotFoundException($"Identity user {request.IdentityUserId} does not exist.");
		if (identityUser.AppUserId != request.ActorUserId) {
			throw new AuthorizationDeniedException(
				$"Actor {request.ActorUserId} may not change credentials for identity user {request.IdentityUserId}.");
		}

		if (request.Enabled && identityUser.AuthenticatorKeyProtected is null) {
			throw new InvariantViolationException("two-factor-key-missing",
				"Two-factor authentication cannot be enabled without an authenticator key.");
		}

		var now = clock.GetCurrentInstant();
		identityUser.TwoFactorEnabled = request.Enabled;
		identityUser.TwoFactorEnabledAt = request.Enabled ? now : null;
		if (!request.Enabled) {
			identityUser.AuthenticatorKeyProtected = null;
		}

		identityUser.SecurityStamp = Guid.NewGuid().ToString("N");
		identityUser.ConcurrencyStamp = Guid.NewGuid().ToString("N");

		_ = await provider.RevokeAllTokensForUserAsync(context, request.ActorUserId, now, cancellationToken)
						  .ConfigureAwait(false);
		AuditEventWriter.Add(
			context,
			request.ActorUserId,
			now,
			request.Enabled ? "authentication.two-factor-enabled" : "authentication.two-factor-disabled",
			"identity_user",
			identityUser.Id,
			request.CorrelationId,
			null,
			null,
			null);

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return ToResult(identityUser);
	}

	public async Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(
		ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);
		_ = await IdentityUserWriteLock.AcquireAsync(context, provider, request.ActorUserId, cancellationToken).ConfigureAwait(false);

		var identityUser = await context.Set<IdentityUserEntity>()
										.FirstOrDefaultAsync(user => user.Id == request.IdentityUserId, cancellationToken).ConfigureAwait(false)
						   ?? throw new EntityNotFoundException($"Identity user {request.IdentityUserId} does not exist.");
		if (identityUser.AppUserId != request.ActorUserId) {
			throw new AuthorizationDeniedException(
				$"Actor {request.ActorUserId} may not change credentials for identity user {request.IdentityUserId}.");
		}

		var now = clock.GetCurrentInstant();
		EnsureCredentialCheckAllowed(identityUser, now);

		var verification = passwordHasher.VerifyHashedPassword(CredentialSubject, identityUser.PasswordHash, request.CurrentPassword);
		if (verification == PasswordVerificationResult.Failed) {
			await RecordPasswordFailureAndCommitAsync(
				context, transaction, identityUser, now, request.CorrelationId, cancellationToken).ConfigureAwait(false);
			throw new InvariantViolationException("account-current-password-incorrect", "The current password is incorrect.");
		}

		identityUser.AccessFailedCount = 0;
		identityUser.LockoutEnd = null;
		identityUser.PasswordHash = passwordHasher.HashPassword(CredentialSubject, request.NewPassword);
		identityUser.RequiresPasswordChange = false;
		identityUser.SecurityStamp = Guid.NewGuid().ToString("N");
		identityUser.ConcurrencyStamp = Guid.NewGuid().ToString("N");

		_ = await provider.RevokeAllTokensForUserAsync(context, request.ActorUserId, now, cancellationToken)
						  .ConfigureAwait(false);
		AuditEventWriter.Add(
			context, request.ActorUserId, now, "authentication.password-change", "identity_user", identityUser.Id,
			request.CorrelationId, null, null, null);

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			SecurityStamp = identityUser.SecurityStamp,
			ConcurrencyStamp = identityUser.ConcurrencyStamp,
		};
	}

	public async Task<EnsurePasskeyUserHandleResult> EnsurePasskeyUserHandleAsync(
		EnsurePasskeyUserHandleRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);
		_ = await IdentityUserWriteLock.AcquireAsync(context, provider, request.ActorUserId, cancellationToken).ConfigureAwait(false);

		var identityUser = await LoadOwnedIdentityUserAsync(context, request.IdentityUserId, request.ActorUserId, cancellationToken)
			.ConfigureAwait(false);

		if (identityUser.PasskeyUserHandle is null) {
			identityUser.PasskeyUserHandle = PasskeyUserHandleGenerator.Create();
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new() {
			UserHandle = identityUser.PasskeyUserHandle,
		};
	}

	public async Task<AddPasskeyResult> AddPasskeyAsync(
		AddPasskeyRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);
		_ = await IdentityUserWriteLock.AcquireAsync(context, provider, request.ActorUserId, cancellationToken).ConfigureAwait(false);

		var identityUser = await LoadOwnedIdentityUserAsync(context, request.IdentityUserId, request.ActorUserId, cancellationToken)
			.ConfigureAwait(false);

		var passkeys = context.Set<IdentityUserPasskeyEntity>();
		var currentCount = await passkeys.CountAsync(p => p.IdentityUserId == identityUser.Id, cancellationToken).ConfigureAwait(false);
		if (currentCount >= PasskeyPolicy.MaxPasskeysPerAccount) {
			throw new InvariantViolationException("passkey-max-count",
				$"An account may hold at most {PasskeyPolicy.MaxPasskeysPerAccount} passkeys.");
		}

		var normalizedName = PasskeyPolicy.Normalize(request.Name);
		var nameTaken = await passkeys
							  .AnyAsync(p => p.IdentityUserId == identityUser.Id && p.NormalizedName == normalizedName, cancellationToken)
							  .ConfigureAwait(false);
		if (nameTaken) {
			throw new InvariantViolationException("passkey-name-duplicate", "A passkey with that name already exists on this account.");
		}

		var now = clock.GetCurrentInstant();
		var credential = request.Credential;
		_ = passkeys.Add(new() {
			CredentialId = credential.CredentialId.ToArray(),
			IdentityUserId = identityUser.Id,
			Name = request.Name.Trim(),
			NormalizedName = normalizedName,
			PublicKey = credential.PublicKey.ToArray(),
			CreatedAt = now,
			SignCount = credential.SignCount,
			Transports = credential.Transports,
			IsUserVerified = credential.IsUserVerified,
			IsBackupEligible = credential.IsBackupEligible,
			IsBackedUp = credential.IsBackedUp,
			AttestationObject = credential.AttestationObject.ToArray(),
			ClientDataJson = credential.ClientDataJson.ToArray(),
			RowVersion = 1,
		});

		RotateStamps(identityUser);
		_ = await provider.RevokeAllTokensForUserAsync(context, request.ActorUserId, now, cancellationToken).ConfigureAwait(false);
		AuditEventWriter.Add(
			context, request.ActorUserId, now, "authentication.passkey-added", "identity_user", identityUser.Id,
			request.CorrelationId, null, null, null);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.UniquenessViolation) {
			throw new ConcurrencyConflictException("A conflicting passkey was enrolled concurrently; retry.", ex);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new() {
			SecurityStamp = identityUser.SecurityStamp,
			ConcurrencyStamp = identityUser.ConcurrencyStamp,
			CredentialId = Base64Url.EncodeToString(credential.CredentialId.Span),
		};
	}

	public async Task<IReadOnlyList<PasskeySummary>> ListPasskeysAsync(
		ListPasskeysRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		var identityUser = await LoadOwnedIdentityUserAsync(context, request.IdentityUserId, request.ActorUserId, cancellationToken)
			.ConfigureAwait(false);

		var summaries = await context.Set<IdentityUserPasskeyEntity>()
									 .Where(p => p.IdentityUserId == identityUser.Id)
									 .OrderByDescending(p => p.CreatedAt)
									 .ThenBy(p => p.NormalizedName)
									 .Select(p => new
									 {
										 p.CredentialId,
										 p.Name,
										 p.CreatedAt,
										 p.IsBackupEligible,
										 p.IsBackedUp,
									 })
									 .ToListAsync(cancellationToken)
									 .ConfigureAwait(false);

		return summaries.ConvertAll(p => new PasskeySummary {
			CredentialId = Base64Url.EncodeToString(p.CredentialId),
			Name = p.Name,
			CreatedAt = p.CreatedAt.ToDateTimeOffset(),
			IsBackupEligible = p.IsBackupEligible,
			IsBackedUp = p.IsBackedUp,
		});
	}

	public async Task<RenamePasskeyResult> RenamePasskeyAsync(
		RenamePasskeyRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);
		_ = await IdentityUserWriteLock.AcquireAsync(context, provider, request.ActorUserId, cancellationToken).ConfigureAwait(false);

		var identityUser = await LoadOwnedIdentityUserAsync(context, request.IdentityUserId, request.ActorUserId, cancellationToken)
			.ConfigureAwait(false);
		var passkey = await LoadOwnedPasskeyAsync(context, request.CredentialId, identityUser.Id, cancellationToken)
			.ConfigureAwait(false);

		var normalizedName = PasskeyPolicy.Normalize(request.NewName);
		var nameTaken = await context.Set<IdentityUserPasskeyEntity>()
									 .AnyAsync(
										 p => p.IdentityUserId == identityUser.Id && p.NormalizedName == normalizedName && p.CredentialId != passkey.CredentialId,
										 cancellationToken)
									 .ConfigureAwait(false);
		if (nameTaken) {
			throw new InvariantViolationException("passkey-name-duplicate", "A passkey with that name already exists on this account.");
		}

		var now = clock.GetCurrentInstant();
		passkey.Name = request.NewName.Trim();
		passkey.NormalizedName = normalizedName;
		++passkey.RowVersion;
		AuditEventWriter.Add(
			context, request.ActorUserId, now, "authentication.passkey-renamed", "identity_user", identityUser.Id,
			request.CorrelationId, null, null, null);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.UniquenessViolation) {
			throw new ConcurrencyConflictException("A conflicting passkey name was set concurrently; retry.", ex);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new() {
			Passkey = new() {
				CredentialId = request.CredentialId,
				Name = passkey.Name,
				CreatedAt = passkey.CreatedAt.ToDateTimeOffset(),
				IsBackupEligible = passkey.IsBackupEligible,
				IsBackedUp = passkey.IsBackedUp,
			},
		};
	}

	public async Task<RemovePasskeyResult> RemovePasskeyAsync(
		RemovePasskeyRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);
		_ = await IdentityUserWriteLock.AcquireAsync(context, provider, request.ActorUserId, cancellationToken).ConfigureAwait(false);

		var identityUser = await LoadOwnedIdentityUserAsync(context, request.IdentityUserId, request.ActorUserId, cancellationToken)
			.ConfigureAwait(false);
		var passkey = await LoadOwnedPasskeyAsync(context, request.CredentialId, identityUser.Id, cancellationToken)
			.ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		_ = context.Set<IdentityUserPasskeyEntity>().Remove(passkey);
		RotateStamps(identityUser);
		_ = await provider.RevokeAllTokensForUserAsync(context, request.ActorUserId, now, cancellationToken).ConfigureAwait(false);
		AuditEventWriter.Add(
			context, request.ActorUserId, now, "authentication.passkey-removed", "identity_user", identityUser.Id,
			request.CorrelationId, null, null, null);

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new() {
			SecurityStamp = identityUser.SecurityStamp,
			ConcurrencyStamp = identityUser.ConcurrencyStamp,
		};
	}

	private static void EnsureCredentialCheckAllowed(IdentityUserEntity identityUser, Instant now)
	{
		if (!identityUser.IsEnabled) {
			throw new InvariantViolationException("account-disabled", "The account is disabled.");
		}

		if (identityUser.LockoutEnabled && identityUser.LockoutEnd is Instant lockoutEnd && lockoutEnd > now) {
			throw new InvariantViolationException("account-locked-out", "The account is temporarily locked out.");
		}
	}

	private static async Task RecordPasswordFailureAndCommitAsync(
		DbContext context,
		IDbContextTransaction transaction,
		IdentityUserEntity identityUser,
		Instant now,
		Guid correlationId,
		CancellationToken cancellationToken)
	{
		++identityUser.AccessFailedCount;
		if (identityUser.LockoutEnabled && identityUser.AccessFailedCount >= AccountLockoutPolicy.MaxFailedAccessAttempts) {
			identityUser.AccessFailedCount = 0;
			identityUser.LockoutEnd = now + AccountLockoutPolicy.LockoutDuration;
			AuditEventWriter.Add(
				context,
				identityUser.AppUserId,
				now,
				"authentication.lockout",
				"identity_user",
				identityUser.Id,
				correlationId,
				null,
				null,
				null);
		}

		identityUser.ConcurrencyStamp = Guid.NewGuid().ToString("N");
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private static void RotateStamps(IdentityUserEntity identityUser)
	{
		identityUser.SecurityStamp = Guid.NewGuid().ToString("N");
		identityUser.ConcurrencyStamp = Guid.NewGuid().ToString("N");
	}

	private static async Task<IdentityUserEntity> LoadOwnedIdentityUserAsync(
		DbContext context, long identityUserId, AppUserId actorUserId, CancellationToken cancellationToken)
	{
		var identityUser = await context.Set<IdentityUserEntity>()
										.FirstOrDefaultAsync(user => user.Id == identityUserId, cancellationToken).ConfigureAwait(false)
						   ?? throw new EntityNotFoundException($"Identity user {identityUserId} does not exist.");
		if (identityUser.AppUserId != actorUserId) {
			throw new AuthorizationDeniedException(
				$"Actor {actorUserId} may not change credentials for identity user {identityUserId}.");
		}

		return identityUser;
	}

	private static async Task<IdentityUserPasskeyEntity> LoadOwnedPasskeyAsync(
		DbContext context, string opaqueCredentialId, long identityUserId, CancellationToken cancellationToken)
	{
		if (!TryDecodeCredentialId(opaqueCredentialId, out var credentialId)) {
			throw new EntityNotFoundException("The passkey does not exist.");
		}

		return await context.Set<IdentityUserPasskeyEntity>()
							.FirstOrDefaultAsync(p => p.CredentialId == credentialId && p.IdentityUserId == identityUserId, cancellationToken)
							.ConfigureAwait(false)
			   ?? throw new EntityNotFoundException("The passkey does not exist.");
	}

	private static bool TryDecodeCredentialId(string opaqueCredentialId, out byte[] credentialId)
	{
		try {
			credentialId = Base64Url.DecodeFromChars(opaqueCredentialId);
			return credentialId.Length > 0;
		}
		catch (FormatException) {
			credentialId = [];
			return false;
		}
	}

	private static SetTwoFactorStateResult ToResult(IdentityUserEntity identityUser) =>
		new() {
			SecurityStamp = identityUser.SecurityStamp,
			ConcurrencyStamp = identityUser.ConcurrencyStamp,
			TwoFactorEnabled = identityUser.TwoFactorEnabled,
			TwoFactorEnabledAt = identityUser.TwoFactorEnabledAt?.ToDateTimeOffset(),
		};
}
