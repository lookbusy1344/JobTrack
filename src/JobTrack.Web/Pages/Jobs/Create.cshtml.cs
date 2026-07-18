namespace JobTrack.Web.Pages.Jobs;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using NodaTime;

/// <summary>
///     Creates a child node under a chosen parent (plan §8.5 slice 3). Carries no page-level
///     authorization policy — <see cref="Domain.Authorization.JobNodeAccessPolicy" /> is re-evaluated
///     against the parent's subtree inside the command itself (plan §8.3), so any authenticated
///     employee may reach this page and let the command's <see cref="AuthorizationDeniedException" />
///     deny it.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class CreateModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private const string ParentHasLeafWorkMessage =
		"This parent already has work attached. Create children only under a node without leaf work.";

	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty(SupportsGet = true)] public long ParentId { get; init; }

	[BindProperty] public CreateInput Input { get; set; } = new();

	public JobNodeDetailResult? Parent { get; private set; }

	public string? ErrorMessage { get; private set; }

	public List<SelectListItem> OwnerOptions { get; private set; } = [];

	/// <summary>
	///     Formats an owner id for display: display name and username when it resolves in
	///     the loaded workflow-employee directory, otherwise a fallback that still names the numeric
	///     id (see <see cref="IJobQueries.GetEmployeeDirectoryAsync" />).
	/// </summary>
	public string DescribeOwnerId(long? ownerUserId) => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, ownerUserId);

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		Input.OwnerUserId = actor.Value.Value;
		Input.Priority = Priority.Medium;

		await LoadParentAsync(actor.Value, cancellationToken);
		await LoadOwnerOptionsAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var context = new CommandContext { Actor = actor.Value, CorrelationId = Guid.NewGuid() };
		await LoadParentAsync(context.Actor, cancellationToken);
		await LoadOwnerOptionsAsync(context.Actor, cancellationToken);

		if (Parent is null || Parent.Node.HasLeafWork || !ModelState.IsValid) {
			return Page();
		}

		var request = new CreateJobNodeRequest {
			Context = context,
			ParentId = new(ParentId),
			Description = Input.Description,
			WriteUp = Input.WriteUp,
			OwnerUserId = Input.OwnerUserId is { } ownerUserId ? new AppUserId(ownerUserId) : null,
			ExpectedDurationHours = Input.ExpectedDurationHours,
			ExpectedCost = Input.ExpectedCost is { } cost ? new Money(cost) : null,
			NeededStart = Input.NeededStart is { } start ? Instant.FromDateTimeOffset(start) : null,
			NeededFinish = Input.NeededFinish is { } finish ? Instant.FromDateTimeOffset(finish) : null,
			Priority = Input.Priority,
		};

		try {
			var result = await jobTrackClient.Jobs.AddChildAsync(request, cancellationToken);

			return RedirectToPage("/Jobs/Browse", new { nodeId = result.Id.Value });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "The parent job node does not exist.";
			await LoadParentAsync(context.Actor, cancellationToken);
			await LoadOwnerOptionsAsync(context.Actor, cancellationToken);
			return Page();
		}
		catch (InvariantViolationException) {
			ErrorMessage = ParentHasLeafWorkMessage;
			await LoadParentAsync(context.Actor, cancellationToken);
			await LoadOwnerOptionsAsync(context.Actor, cancellationToken);
			return Page();
		}
	}

	private async Task LoadParentAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			Parent = await jobTrackClient.Query.GetJobNodeAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = new JobNodeId(ParentId) }, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "The parent job node does not exist.";
		}

		if (Parent is { Node.HasLeafWork: true }) {
			ErrorMessage = ParentHasLeafWorkMessage;
		}
	}

	private async Task LoadOwnerOptionsAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		var directory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() } },
			cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		OwnerOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Unassigned", string.Empty));
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class CreateInput
	{
		[Required] public string Description { get; set; } = string.Empty;

		public string? WriteUp { get; set; }

		public long? OwnerUserId { get; set; }

		public decimal? ExpectedDurationHours { get; set; }

		public decimal? ExpectedCost { get; set; }

		public DateTimeOffset? NeededStart { get; set; }

		public DateTimeOffset? NeededFinish { get; set; }

		[Required] public Priority Priority { get; set; }
	}
}
