namespace JobTrack.AdminCli;

using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     The <c>create-employee</c> command: provisions a normal (non-administrator) employee under an
///     existing administrator actor via <see cref="IEmployeeCommands.CreateEmployeeAsync" />, granting
///     the first <c>--roles</c> entry as the initial role and any remainder through
///     <see cref="IEmployeeCommands.AssignRoleAsync" />. With <c>--no-force-password-change</c> it then
///     clears the ADR 0023 forced-password-change flag on the new account, so a published/shared
///     credential (e.g. the container demo's <c>demo</c> account) stays usable without a forced change
///     on first sign-in. It does not reimplement any of these operations — the transactional semantics
///     live in the application layer and are covered by <c>EmployeeCommandsTests</c>.
/// </summary>
public static class CreateEmployeeCommand
{
	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CreateEmployeeCommandOptions options,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(jobTrackClient);
		ArgumentNullException.ThrowIfNull(options);

		var actor = await userManager.FindByNameAsync(options.ActorUsername).ConfigureAwait(false);
		if (actor is null) {
			io.WriteError($"No administrator account found for username '{options.ActorUsername}'.");
			return 1;
		}

		var context = new CommandContext { Actor = actor.AppUserId, CorrelationId = Guid.NewGuid() };

		try {
			var created = await jobTrackClient.Employees.CreateEmployeeAsync(
				new() {
					Context = context,
					DisplayName = options.DisplayName,
					IanaTimeZone = options.IanaTimeZone,
					DefaultHourlyRate = options.DefaultHourlyRate is decimal rate ? new HourlyRate(rate) : null,
					UserName = options.Username,
					Password = options.Password,
					Role = options.Roles[0],
				},
				cancellationToken).ConfigureAwait(false);

			foreach (var role in options.Roles.Skip(1)) {
				_ = await jobTrackClient.Employees.AssignRoleAsync(
					new() { Context = context, TargetUserId = created.Id, Role = role },
					cancellationToken).ConfigureAwait(false);
			}

			if (!options.ForcePasswordChange && !await ForcedPasswordChange.ClearAsync(io, userManager, options.Username).ConfigureAwait(false)) {
				return 1;
			}

			io.WriteLine(
				$"Created employee '{options.Username}' (id {created.Id.Value}) with role(s): {string.Join(", ", options.Roles)}.");
			return 0;
		}
		catch (JobTrackException ex) {
			io.WriteError($"Failed to create employee '{options.Username}': {ex.Message}");
			return 1;
		}
	}
}
