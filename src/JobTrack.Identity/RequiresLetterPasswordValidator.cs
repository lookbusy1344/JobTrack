namespace JobTrack.Identity;

using Abstractions;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     Requires at least one letter of either case (<see cref="PasswordPolicy" />).
///     <c>IdentityOptions.Password</c> only exposes separate <c>RequireLowercase</c>/
///     <c>RequireUppercase</c> flags, both of which are disabled -- neither expresses "any case is
///     fine as long as there's a letter", so that half of the policy is enforced here instead.
/// </summary>
public sealed class RequiresLetterPasswordValidator : IPasswordValidator<JobTrackIdentityUser>
{
	public Task<IdentityResult> ValidateAsync(UserManager<JobTrackIdentityUser> manager, JobTrackIdentityUser user, string? password)
	{
		var hasLetter = password is not null && password.Any(char.IsLetter);

		return Task.FromResult(hasLetter
			? IdentityResult.Success
			: IdentityResult.Failed(new IdentityError {
				Code = "PasswordRequiresLetter",
				Description = "Passwords must have at least one letter ('a'-'z' or 'A'-'Z').",
			}));
	}
}
