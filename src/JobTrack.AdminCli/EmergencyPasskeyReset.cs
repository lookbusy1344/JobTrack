namespace JobTrack.AdminCli;

using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     The <c>reset-passkeys</c> command (ADR 0071 §8.4): a database-backed emergency mechanism for
///     removing every passkey from an account after a lost or compromised device, when the normal web
///     administration flow cannot be used -- including the bootstrap administrator account itself, since
///     this never depends on being able to authenticate first. Mirrors
///     <see cref="EmergencyTwoFactorReset" />'s shape and runs under the same narrower
///     <c>jobtrack_emergency_reset</c> PostgreSQL role. It removes passkeys only: the password and any
///     TOTP enrolment are left untouched. Rotating the security stamp ends any live session, and every
///     personal access token is revoked, matching the credential-sensitivity class of the other
///     emergency resets.
/// </summary>
public static class EmergencyPasskeyReset
{
	private const string AuditOperation = "emergency-passkey-reset";
	private const string AuditEntityType = "identity_user";
	private const string AuditReason = "Operator-initiated emergency passkey reset via JobTrack.AdminCli.";

	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		JobTrackIdentityDbContext identityContext,
		AdminCliProvider provider,
		string username,
		IClock clock,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(identityContext);
		ArgumentNullException.ThrowIfNull(clock);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);

		var user = await userManager.FindByNameAsync(username);
		if (user is null) {
			io.WriteError($"No employee account found for username '{username}'.");
			return 1;
		}

		await using var transaction = await identityContext.Database.BeginTransactionAsync(cancellationToken);

		var removedCount = await identityContext.Database.ExecuteSqlInterpolatedAsync(
			$"DELETE FROM identity_user_passkey WHERE identity_user_id = {user.Id};", cancellationToken);

		user.SecurityStamp = Guid.NewGuid().ToString("N");
		// See EmergencyTwoFactorReset: a locked-out account is a common reason to reach for an emergency
		// command, and this one must not hand back an account that still cannot sign in with its password.
		user.LockoutEnd = null;
		user.AccessFailedCount = 0;

		var updateResult = await userManager.UpdateAsync(user);
		if (!updateResult.Succeeded) {
			io.WriteError($"Failed to update the account: {string.Join("; ", updateResult.Errors.Select(e => e.Description))}");
			return 1;
		}

		var correlationId = Guid.NewGuid();
		// See EmergencyPasswordReset for why this is a new, narrow, one-off insert rather than a shared
		// audit writer, and why occurred_at needs a provider-specific value. No credential detail is
		// recorded (ADR 0071 §9): the audit row carries actor/target/correlation only.
		if (provider == AdminCliProvider.Sqlite) {
			var occurredAtTicks = clock.GetCurrentInstant().ToUnixTimeTicks();
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"""
				 INSERT INTO audit_event (occurred_at, actor_user_id, operation, entity_type, entity_id, correlation_id, reason)
				 VALUES ({occurredAtTicks}, {user.AppUserId.Value}, {AuditOperation}, {AuditEntityType}, {user.Id}, {correlationId}, {AuditReason});
				 """,
				cancellationToken);
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"UPDATE personal_access_token SET revoked_at = {occurredAtTicks} WHERE app_user_id = {user.AppUserId.Value} AND revoked_at IS NULL;",
				cancellationToken);
		} else {
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"""
				 INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id, reason)
				 VALUES ({user.AppUserId.Value}, {AuditOperation}, {AuditEntityType}, {user.Id}, {correlationId}, {AuditReason});
				 """,
				cancellationToken);
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"UPDATE personal_access_token SET revoked_at = now() WHERE app_user_id = {user.AppUserId.Value} AND revoked_at IS NULL;",
				cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);

		io.WriteLine(
			$"Removed {removedCount} passkey{(removedCount == 1 ? string.Empty : "s")} from '{username}'. " +
			"Their password and any two-factor enrolment are unchanged.");
		return 0;
	}
}
