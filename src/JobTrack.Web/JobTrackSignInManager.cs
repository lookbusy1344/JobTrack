namespace JobTrack.Web;

using Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

/// <summary>
///     Blocks sign-in for a disabled account (<see cref="JobTrackIdentityUser.IsEnabled" />) — threat-model
///     row 3 (session theft: a former employee's disabled account must not admit a new session).
///     <see cref="SignInManager{TUser}.PasswordSignInAsync(TUser, string, bool, bool)" /> checks
///     <see cref="CanSignInAsync" /> in its <c>PreSignInCheck</c> before verifying the password, so a
///     disabled account's login attempt returns <see cref="SignInResult.NotAllowed" /> — the Login page
///     already renders the same generic failure message for every non-success result, so this needs no
///     page changes and keeps the no-enumeration guarantee (threat-model row 2) for disabled accounts too.
///     Lives in <c>JobTrack.Web</c>, not <c>JobTrack.Identity</c>, because <see cref="SignInManager{TUser}" />
///     needs the ASP.NET Core shared framework that project deliberately does not reference (ADR 0022).
/// </summary>
public sealed class JobTrackSignInManager(
	UserManager<JobTrackIdentityUser> userManager,
	IHttpContextAccessor contextAccessor,
	IUserClaimsPrincipalFactory<JobTrackIdentityUser> claimsFactory,
	IOptions<IdentityOptions> optionsAccessor,
	ILogger<SignInManager<JobTrackIdentityUser>> logger,
	IAuthenticationSchemeProvider schemes,
	IUserConfirmation<JobTrackIdentityUser> confirmation) :
	SignInManager<JobTrackIdentityUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
	public override async Task<bool> CanSignInAsync(JobTrackIdentityUser user) =>
		user.IsEnabled && await base.CanSignInAsync(user);
}
