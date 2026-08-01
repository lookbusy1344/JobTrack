namespace JobTrack.Web;

using System.Security.Claims;
using Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NodaTime;

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
	IUserConfirmation<JobTrackIdentityUser> confirmation,
	IClock clock) :
	SignInManager<JobTrackIdentityUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
	public override async Task<bool> CanSignInAsync(JobTrackIdentityUser user) =>
		user.IsEnabled && await base.CanSignInAsync(user);

	/// <summary>
	///     ADR 0057: every public sign-in entry point in the base <see cref="SignInManager{TUser}" />
	///     (<c>PasswordSignInAsync</c>, <c>TwoFactorAuthenticatorSignInAsync</c>, <c>RefreshSignInAsync</c>)
	///     funnels through this one overload. Stamp the session's absolute-ceiling origin — preserved from
	///     the existing ticket if there is one, otherwise <paramref name="authenticationProperties" />'s
	///     session is brand new and origin becomes now — and the recent-authentication freshness anchor,
	///     which is always now: every call reaching this method already represents a fresh-authentication
	///     event (a password/two-factor check just succeeded, or a caller such as
	///     <c>ChangePassword</c>/<c>ManageTwoFactor</c>/<c>ConfirmAccess</c> independently verified the
	///     current password or TOTP code before calling <c>RefreshSignInAsync</c>).
	/// </summary>
	public override async Task SignInWithClaimsAsync(
		JobTrackIdentityUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
	{
		authenticationProperties ??= new();
		var now = clock.GetCurrentInstant();
		var existing = await Context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
		var origin = SessionAuthenticationInstants.TryGetOrigin(existing.Properties) ?? now;
		SessionAuthenticationInstants.Stamp(authenticationProperties, origin, now);

		await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
	}
}
