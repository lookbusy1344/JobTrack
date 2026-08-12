namespace JobTrack.AdminCli;

using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     The <c>set-home-node</c> command: points an existing employee's post-login landing node at a
///     branch, or clears it back to the tree root, via <see cref="IEmployeeCommands.SetHomeNodeAsync" />.
///     That command is self-service only — there is no administrator path to another employee's home
///     node, and none is needed since the preference carries no ownership or authorization weight — so
///     this runs as the named employee themselves rather than under a separate <c>--actor</c>.
/// </summary>
public static class SetHomeNodeCommand
{
	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		string username,
		JobNodeId? nodeId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(jobTrackClient);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);

		var user = await userManager.FindByNameAsync(username);
		if (user is null) {
			io.WriteError($"No employee account found for username '{username}'.");
			return 1;
		}

		try {
			_ = await jobTrackClient.Employees.SetHomeNodeAsync(
				new() {
					Context = new() {
						Actor = user.AppUserId,
						CorrelationId = Guid.NewGuid(),
					},
					NodeId = nodeId,
				},
				cancellationToken);
		}
		catch (JobTrackException ex) {
			io.WriteError($"Failed to set the home node for '{username}': {ex.Message}");
			return 1;
		}

		io.WriteLine(nodeId is JobNodeId home
			? $"Set job node {home.Value} as the home node for '{username}'."
			: $"Cleared the home node for '{username}'; sign-in now lands at the tree root.");
		return 0;
	}
}
