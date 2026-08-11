namespace JobTrack.Web;

using System.Text.Encodings.Web;
using Application;
using Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
///     The bearer authentication scheme's name and the shared "is this a bearer request?" test (ADR
///     0029) — used both to route a request to this scheme (<c>Program.cs</c>'s policy scheme
///     selector) and to exempt bearer-authenticated requests from antiforgery validation
///     (<see cref="JobTrackApi" />'s <c>AntiforgeryValidationFilter</c>): a bearer token is never
///     attached by a browser automatically, so it carries none of the ambient-credential risk
///     antiforgery tokens exist to mitigate.
/// </summary>
internal static class PersonalAccessTokenAuthenticationDefaults
{
	public const string AuthenticationScheme = "PersonalAccessToken";
	private const string BearerPrefix = "Bearer ";

	public static bool IsBearerRequest(HttpContext httpContext)
	{
		ArgumentNullException.ThrowIfNull(httpContext);

		return httpContext.Request.Headers.Authorization.ToString()
			.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);
	}
}

/// <summary>
///     Authenticates <c>Authorization: Bearer &lt;token&gt;</c> requests against
///     <see cref="ITokenCommands.TryAuthenticateAsync" /> and builds the identical claims principal the
///     cookie scheme produces (ADR 0029), so every downstream authorization policy, ownership check,
///     and data-sensitivity check runs the same regardless of which scheme authenticated the caller.
/// </summary>
internal sealed class PersonalAccessTokenAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder,
	IJobTrackClient jobTrackClient,
	JobTrackUserStore userStore,
	IUserClaimsPrincipalFactory<JobTrackIdentityUser> claimsPrincipalFactory)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	private const string BearerPrefix = "Bearer ";
	private const string InvalidTokenFailureMessage = "Invalid, expired, or revoked personal access token.";

	protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var header = Request.Headers.Authorization.ToString();
		if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) {
			return AuthenticateResult.NoResult();
		}

		var token = header[BearerPrefix.Length..].Trim();
		if (token.Length == 0) {
			return AuthenticateResult.Fail("No bearer token supplied.");
		}

		var authenticated = await jobTrackClient.Tokens.TryAuthenticateAsync(
			new() { Token = token }, Context.RequestAborted);
		if (authenticated is null) {
			return AuthenticateResult.Fail(InvalidTokenFailureMessage);
		}

		var identityUser = await userStore.FindByAppUserIdAsync(authenticated.UserId, Context.RequestAborted);
		if (identityUser is null || identityUser.RequiresPasswordChange) {
			return AuthenticateResult.Fail(InvalidTokenFailureMessage);
		}

		var principal = await claimsPrincipalFactory.CreateAsync(identityUser);

		return AuthenticateResult.Success(new(principal, Scheme.Name));
	}

	// The base handler's default challenge is a bare 401 with no body -- inconsistent with the
	// cookie scheme's JobTrackApi.HandleRedirectAsync, which already returns problem-details JSON
	// for /api/* requests (remediation plan §3.3). Every bearer failure reason maps to the same
	// generic body so a caller cannot distinguish why authentication failed.
	protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
	{
		Response.StatusCode = StatusCodes.Status401Unauthorized;
		await Response.WriteAsJsonAsync(
			new ProblemDetails {
				Status = StatusCodes.Status401Unauthorized,
				Title = "Authentication required",
				Detail = "Authenticate and retry.",
				Type = JobTrackApi.AuthenticationProblemType,
			},
			options: null,
			contentType: "application/problem+json");
	}

	// A role-policy denial (e.g. RateRead requiring Administrator/CostViewer) is rejected by the
	// ASP.NET Core authorization middleware before any endpoint handler runs, so JobTrackApi's own
	// AuthorizationDeniedException catch clause never sees it -- the base handler's default forbid
	// is a bare 403 with no body. This mirrors HandleChallengeAsync above so a bearer 403 is
	// problem-details JSON too, matching the cookie scheme's OnRedirectToAccessDenied (remediation
	// plan §3.4) and never disclosing the denied resource's data.
	protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
	{
		Response.StatusCode = StatusCodes.Status403Forbidden;
		await Response.WriteAsJsonAsync(
			new ProblemDetails {
				Status = StatusCodes.Status403Forbidden,
				Title = "Forbidden",
				Detail = "You do not have permission to perform this action.",
				Type = JobTrackApi.ForbiddenProblemType,
			},
			options: null,
			contentType: "application/problem+json");
	}
}
