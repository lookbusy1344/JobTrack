namespace JobTrack.AdminCli;

using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     The <c>reset-2fa</c> command (ADR 0037): a database-backed emergency mechanism for clearing an
///     employee's TOTP two-factor enrolment when they have lost their authenticator device and the
///     normal web administration flow cannot be used -- including the bootstrap administrator account
///     itself, since this never depends on being able to authenticate first. Mirrors
///     <see cref="EmergencyPasswordReset" />'s shape: uses <c>JobTrack.Identity</c> directly, under the
///     same narrower <c>jobtrack_emergency_reset</c> PostgreSQL role.
/// </summary>
public static class EmergencyTwoFactorReset
{
	private const string AuditOperation = "emergency-two-factor-reset";
	private const string AuditEntityType = "identity_user";
	private const string AuditReason = "Operator-initiated emergency two-factor reset via JobTrack.AdminCli.";

	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		JobTrackIdentityDbContext identityContext,
		AdminCliProvider provider,
		string username,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(identityContext);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);

		var user = await userManager.FindByNameAsync(username).ConfigureAwait(false);
		if (user is null) {
			io.WriteError($"No employee account found for username '{username}'.");
			return 1;
		}

		await using var transaction = await identityContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		user.TwoFactorEnabled = false;
		user.AuthenticatorKeyProtected = null;
		user.TwoFactorEnabledAt = null;
		user.SecurityStamp = Guid.NewGuid().ToString("N");

		var updateResult = await userManager.UpdateAsync(user).ConfigureAwait(false);
		if (!updateResult.Succeeded) {
			io.WriteError($"Failed to update the account: {string.Join("; ", updateResult.Errors.Select(e => e.Description))}");
			return 1;
		}

		var correlationId = Guid.NewGuid();
		// See EmergencyPasswordReset for why this is a new, narrow, one-off insert rather than a
		// shared audit writer, and why occurred_at needs a provider-specific value.
		if (provider == AdminCliProvider.Sqlite) {
			var occurredAtTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"""
				 INSERT INTO audit_event (occurred_at, actor_user_id, operation, entity_type, entity_id, correlation_id, reason)
				 VALUES ({occurredAtTicks}, {user.AppUserId.Value}, {AuditOperation}, {AuditEntityType}, {user.Id}, {correlationId}, {AuditReason});
				 """,
				cancellationToken).ConfigureAwait(false);
			// ADR 0029: an emergency two-factor reset is the same credential-sensitivity class as an
			// administrator-driven reset -- it must revoke every live personal access token too.
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"UPDATE personal_access_token SET revoked_at = {occurredAtTicks} WHERE app_user_id = {user.AppUserId.Value} AND revoked_at IS NULL;",
				cancellationToken).ConfigureAwait(false);
		} else {
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"""
				 INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id, reason)
				 VALUES ({user.AppUserId.Value}, {AuditOperation}, {AuditEntityType}, {user.Id}, {correlationId}, {AuditReason});
				 """,
				cancellationToken).ConfigureAwait(false);
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"UPDATE personal_access_token SET revoked_at = now() WHERE app_user_id = {user.AppUserId.Value} AND revoked_at IS NULL;",
				cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		io.WriteLine(
			$"Two-factor authentication has been reset for '{username}'. " +
			"They can now sign in with their password alone, and re-enrol if they choose.");
		return 0;
	}
}
