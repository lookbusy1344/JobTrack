namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

internal sealed class PostgreSqlAccountCredentialPort : IAccountCredentialPort
{
	private static readonly EmployeeCredentialSubject CredentialSubject = new();

	private readonly IClock clock;
	private readonly NpgsqlDataSource dataSource;
	private readonly IPasswordHasher<EmployeeCredentialSubject> passwordHasher;

	public PostgreSqlAccountCredentialPort(NpgsqlDataSource dataSource, IClock clock, IPasswordHasher<EmployeeCredentialSubject> passwordHasher)
	{
		this.dataSource = dataSource;
		this.clock = clock;
		this.passwordHasher = passwordHasher;
	}

	public async Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
		SetTwoFactorStateRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

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

		_ = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.ActorUserId, now, cancellationToken).ConfigureAwait(false);
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
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var identityUser = await context.Set<IdentityUserEntity>()
							   .FirstOrDefaultAsync(user => user.Id == request.IdentityUserId, cancellationToken).ConfigureAwait(false)
						   ?? throw new EntityNotFoundException($"Identity user {request.IdentityUserId} does not exist.");
		if (identityUser.AppUserId != request.ActorUserId) {
			throw new AuthorizationDeniedException(
				$"Actor {request.ActorUserId} may not change credentials for identity user {request.IdentityUserId}.");
		}

		var verification = passwordHasher.VerifyHashedPassword(CredentialSubject, identityUser.PasswordHash, request.CurrentPassword);
		if (verification == PasswordVerificationResult.Failed) {
			throw new InvariantViolationException("account-current-password-incorrect", "The current password is incorrect.");
		}

		var now = clock.GetCurrentInstant();
		identityUser.PasswordHash = passwordHasher.HashPassword(CredentialSubject, request.NewPassword);
		identityUser.RequiresPasswordChange = false;
		identityUser.SecurityStamp = Guid.NewGuid().ToString("N");
		identityUser.ConcurrencyStamp = Guid.NewGuid().ToString("N");

		_ = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.ActorUserId, now, cancellationToken).ConfigureAwait(false);
		AuditEventWriter.Add(
			context, request.ActorUserId, now, "authentication.password-change", "identity_user", identityUser.Id,
			request.CorrelationId, null, null, null);

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() { SecurityStamp = identityUser.SecurityStamp, ConcurrencyStamp = identityUser.ConcurrencyStamp };
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static SetTwoFactorStateResult ToResult(IdentityUserEntity identityUser) =>
		new() {
			SecurityStamp = identityUser.SecurityStamp,
			ConcurrencyStamp = identityUser.ConcurrencyStamp,
			TwoFactorEnabled = identityUser.TwoFactorEnabled,
			TwoFactorEnabledAt = identityUser.TwoFactorEnabledAt?.ToDateTimeOffset(),
		};
}
