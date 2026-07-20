namespace JobTrack.AdminCli;

using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using NodaTime;

/// <summary>
///     The <c>issue-token</c> command: mints a bearer personal access token for an existing employee
///     account without a signed-in browser session — needed for scripting/tooling (e.g. hurl-driven
///     smoke tests of the external HTTP API) since the only other issuance path is the self-service
///     <c>PersonalAccessTokensModel</c> Razor page (ADR 0029). Resolves the username to its
///     <see cref="AppUserId" /> via <see cref="UserManager{TUser}" /> — the same lookup
///     <see cref="EmergencyPasswordReset" /> uses — then issues through <see cref="IJobTrackClient" />,
///     since <see cref="ITokenCommands.IssueAsync" /> only ever issues a token for the actor themselves.
/// </summary>
public static class IssueTokenCommand
{
	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		string username,
		string label,
		Duration lifetime,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(jobTrackClient);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		ArgumentException.ThrowIfNullOrWhiteSpace(label);

		var user = await userManager.FindByNameAsync(username).ConfigureAwait(false);
		if (user is null) {
			io.WriteError($"No employee account found for username '{username}'.");
			return 1;
		}

		try {
			var result = await jobTrackClient.Tokens.IssueAsync(
				new() {
					Context = new() { Actor = user.AppUserId, CorrelationId = Guid.NewGuid() },
					TargetUserId = user.AppUserId,
					Label = label,
					Lifetime = lifetime,
				},
				cancellationToken).ConfigureAwait(false);

			io.WriteLine($"Personal access token for '{username}': {result.Token}");
			io.WriteLine("Relay this credential out-of-band now -- it will not be shown again.");
			return 0;
		}
		catch (InvariantViolationException ex) {
			io.WriteError($"Failed to issue token: {ex.Message}");
			return 1;
		}
	}
}
