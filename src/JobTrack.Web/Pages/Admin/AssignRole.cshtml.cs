namespace JobTrack.Web.Pages.Admin;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

/// <summary>
///     §8.3/§8.5 slice 10 (minimal cut): grants or revokes one of the six baseline roles for another
///     employee. Administrator-only. <see cref="AssignRoleInput" /> is a narrow, allow-listed request
///     model bound from the form — it never binds directly to <see cref="JobTrackIdentityUser" /> or any
///     domain/persistence entity (threat-model row 9: mass assignment).
/// </summary>
[Authorize(Policy = EmployeeRoleNames.Administrator)]
public sealed class AssignRoleModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty] public AssignRoleInput Input { get; set; } = new();

	public string? ErrorMessage { get; private set; }

	public string? SuccessMessage { get; private set; }

	public List<SelectListItem> TargetUserOptions { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadTargetUserOptionsAsync(cancellationToken);

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		if (!ModelState.IsValid) {
			await LoadTargetUserOptionsAsync(cancellationToken);
			return Page();
		}

		var actor = await userManager.GetUserAsync(User);
		if (actor is null) {
			return Challenge();
		}

		await LoadTargetUserOptionsAsync(cancellationToken);

		var context = new CommandContext { Actor = actor.AppUserId, CorrelationId = Guid.NewGuid() };
		var targetUserId = new AppUserId(Input.TargetUserId);

		try {
			var result = Input.Revoke
				? await jobTrackClient.Employees.RevokeRoleAsync(new() { Context = context, TargetUserId = targetUserId, Role = Input.Role },
					cancellationToken)
				: await jobTrackClient.Employees.AssignRoleAsync(new() { Context = context, TargetUserId = targetUserId, Role = Input.Role },
					cancellationToken);

			SuccessMessage =
				$"{EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, targetUserId.Value, "That employee")} " +
				$"now holds: {string.Join(", ", result.Roles.Select(role => EnumDisplay.Label(role)))}.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That employee does not exist.";
		}

		return Page();
	}

	private async Task LoadTargetUserOptionsAsync(CancellationToken cancellationToken)
	{
		var actor = await userManager.GetUserAsync(User);
		if (actor is null) {
			return;
		}

		var directory = await jobTrackClient.Query.GetAllEmployeesAsync(
			new() { Context = new() { Actor = actor.AppUserId, CorrelationId = Guid.NewGuid() } }, cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		TargetUserOptions = EmployeeDirectoryDisplay.BuildOptions(directory);
	}

	public sealed class AssignRoleInput
	{
		[Required] public long TargetUserId { get; init; }

		[Required] public EmployeeRole Role { get; init; }

		public bool Revoke { get; init; }
	}
}
