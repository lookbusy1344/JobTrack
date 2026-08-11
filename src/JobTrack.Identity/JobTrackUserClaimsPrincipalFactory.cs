namespace JobTrack.Identity;

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

/// <summary>
///     Adds role claims to the sign-in principal that <see cref="UserClaimsPrincipalFactory{TUser}" />
///     alone does not: the base single-type-parameter factory never queries role membership — only the
///     two-type-parameter <c>UserClaimsPrincipalFactory&lt;TUser, TRole&gt;</c> does, and that overload
///     requires a <c>RoleManager&lt;TRole&gt;</c>/generic Identity role type this project deliberately
///     does not have (ADR 0022). This override queries <see cref="JobTrackUserStore" /> directly via
///     <see cref="UserManager{TUser}.GetRolesAsync" /> instead, so role-based
///     <c>[Authorize(Policy = ...)]</c> checks (plan §8.3) see the roles <see cref="JobTrackUserStore" />
///     reports.
/// </summary>
internal sealed class JobTrackUserClaimsPrincipalFactory(
	UserManager<JobTrackIdentityUser> userManager,
	IOptions<IdentityOptions> optionsAccessor) :
	UserClaimsPrincipalFactory<JobTrackIdentityUser>(userManager, optionsAccessor)
{
	protected override async Task<ClaimsIdentity> GenerateClaimsAsync(JobTrackIdentityUser user)
	{
		var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

		if (UserManager.SupportsUserRole) {
			var roles = await UserManager.GetRolesAsync(user).ConfigureAwait(false);
			identity.AddClaims(roles.Select(role => new Claim(Options.ClaimsIdentity.RoleClaimType, role)));
		}

		return identity;
	}
}
