namespace JobTrack.AdminCli;

using Identity;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     Clears an account's forced-password-change flag (the ADR 0023 default that
///     <c>bootstrap</c>/<c>create-employee</c> otherwise leave set). Shared by both commands' opt-in
///     <c>--no-force-password-change</c> path, whose sole use case is a published/shared demo credential
///     that must stay usable without a forced change on first sign-in — never a normal provisioning
///     default.
/// </summary>
internal static class ForcedPasswordChange
{
	/// <summary>
	///     Reloads <paramref name="username" /> via <paramref name="userManager" /> and clears its
	///     forced-password-change flag. Returns <see langword="true" /> on success; on failure writes the
	///     Identity errors to <paramref name="io" /> and returns <see langword="false" /> so the caller can
	///     fail the command rather than emit an account that still forces a change.
	/// </summary>
	public static async Task<bool> ClearAsync(IConsoleIO io, UserManager<JobTrackIdentityUser> userManager, string username)
	{
		var user = await userManager.FindByNameAsync(username).ConfigureAwait(false)
				   ?? throw new InvalidOperationException($"Account '{username}' could not be reloaded to clear its forced password change.");

		user.RequiresPasswordChange = false;
		var result = await userManager.UpdateAsync(user).ConfigureAwait(false);
		if (result.Succeeded) {
			return true;
		}

		io.WriteError(
			$"Account '{username}' was created but clearing its forced password change failed: " +
			string.Join("; ", result.Errors.Select(e => e.Description)));
		return false;
	}
}
