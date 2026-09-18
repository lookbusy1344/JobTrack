namespace JobTrack.Web;

using System.Globalization;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     Applies one lockout and request-throttling policy to reusable credentials presented by an
///     already-authenticated account. The account backstop deliberately excludes the remote address.
/// </summary>
public sealed class CurrentCredentialVerifier(
	UserManager<JobTrackIdentityUser> userManager,
	ILoginAttemptRateLimiter loginAttemptRateLimiter,
	IJobTrackClient jobTrackClient)
{
	public ValueTask<RateLimitOutcome> TryAcquireAsync(
		string action,
		JobTrackIdentityUser user,
		string remoteAddress,
		CancellationToken cancellationToken)
	{
		var userId = user.Id.ToString(CultureInfo.InvariantCulture);
		return loginAttemptRateLimiter.TryAcquireAsync(
			$"current-credential:{action}:request:{remoteAddress}:{userId}",
			$"current-credential:{action}:account:{userId}",
			cancellationToken);
	}

	public async Task<SignInResult> CheckPasswordAsync(
		JobTrackIdentityUser user,
		string password,
		bool resetOnSuccess)
	{
		var precheck = await PrecheckAsync(user);
		if (precheck is not null) {
			return precheck;
		}

		if (!await userManager.CheckPasswordAsync(user, password)) {
			var result = await RecordFailureAsync(user);
			if (result.IsLockedOut) {
				await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, AuthenticationAuditEventKind.Lockout);
			}

			return result;
		}

		if (resetOnSuccess) {
			await ResetFailuresAsync(user);
		}

		return SignInResult.Success;
	}

	public async Task<SignInResult> CheckTwoFactorAsync(JobTrackIdentityUser user, string code)
	{
		var precheck = await PrecheckAsync(user);
		if (precheck is not null) {
			return precheck;
		}

		if (!await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)) {
			var result = await RecordFailureAsync(user);
			var eventKind = result.IsLockedOut
				? AuthenticationAuditEventKind.Lockout
				: AuthenticationAuditEventKind.TwoFactorFailed;
			await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, eventKind);
			return result;
		}

		await ResetFailuresAsync(user);
		return SignInResult.Success;
	}

	private async Task<SignInResult?> PrecheckAsync(JobTrackIdentityUser user)
	{
		if (!user.IsEnabled) {
			return SignInResult.NotAllowed;
		}

		return await userManager.IsLockedOutAsync(user) ? SignInResult.LockedOut : null;
	}

	private async Task<SignInResult> RecordFailureAsync(JobTrackIdentityUser user)
	{
		var result = await userManager.AccessFailedAsync(user);
		EnsureSucceeded(result, "record the current-credential failure");
		return await userManager.IsLockedOutAsync(user) ? SignInResult.LockedOut : SignInResult.Failed;
	}

	private async Task ResetFailuresAsync(JobTrackIdentityUser user)
	{
		if (user.AccessFailedCount == 0) {
			return;
		}

		var result = await userManager.ResetAccessFailedCountAsync(user);
		EnsureSucceeded(result, "reset the current-credential failure count");
	}

	private static void EnsureSucceeded(IdentityResult result, string operation)
	{
		if (!result.Succeeded) {
			throw new InvalidOperationException($"Identity could not {operation}.");
		}
	}
}
