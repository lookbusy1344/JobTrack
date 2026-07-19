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
///     Replaces a node's editable fields (plan §8.5 slice 3). Concurrency-conflict recovery:
///     <see cref="IJobCommands.EditAsync" /> is a full-replace compare-and-swap on
///     <see cref="EditJobNodeRequest.Version" />, so a <see cref="ConcurrencyConflictException" /> means
///     another actor changed the node after this page loaded it. Recovery re-reads the current node,
///     keeps the user's attempted edits in the form so nothing typed is lost, and refreshes the
///     version for another save attempt.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class EditModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private Dictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById = [];

	[BindProperty(SupportsGet = true)] public long NodeId { get; init; }

	[BindProperty] public long OriginalVersion { get; set; }

	[BindProperty] public EditInput Input { get; set; } = new();

	public JobNodeDetailResult? CurrentNode { get; private set; }

	public string? ErrorMessage { get; private set; }

	/// <summary>
	///     Owner dropdown options for the edit form, built from
	///     <see cref="IJobQueries.GetEmployeeDirectoryAsync" /> (display name and username, not a bare
	///     id — see <see cref="Browse.BrowseModel.DescribeOwner" /> for the read-only equivalent), plus
	///     an "Unassigned" option since ownership is nullable (ownership model §2.1).
	/// </summary>
	public List<SelectListItem> OwnerOptions { get; private set; } = [];

	/// <summary>
	///     Formats an owner id for display: display name and username when it resolves in
	///     the loaded directory, otherwise a fallback that still names the numeric id (covers an owner
	///     disabled or role-revoked since assignment — see
	///     <see cref="Application.IJobQueries.GetEmployeeDirectoryAsync" />).
	/// </summary>
	public string DescribeOwnerId(long? ownerUserId) => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, ownerUserId);

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadCurrentNodeAsync(actor.Value, cancellationToken);
		await LoadOwnerOptionsAsync(actor.Value, cancellationToken);
		if (CurrentNode is { } node) {
			Input.Description = node.Node.Description;
			Input.WriteUp = node.Node.WriteUp;
			Input.OwnerUserId = node.Node.OwnerUserId?.Value;
			Input.Priority = node.Node.Priority;
			Input.ExpectedDurationHours = node.Node.ExpectedDurationHours;
			Input.ExpectedCost = node.Node.ExpectedCost?.Amount;
			Input.NeededStart = node.Node.NeededStart?.ToDateTimeOffset();
			Input.NeededFinish = node.Node.NeededFinish?.ToDateTimeOffset();
			OriginalVersion = node.Node.Version;
		}

		return Page();
	}

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadCurrentNodeAsync(actor.Value, cancellationToken);
		await LoadOwnerOptionsAsync(actor.Value, cancellationToken);

		if (CurrentNode is null || !ModelState.IsValid) {
			return Page();
		}

		var context = new CommandContext { Actor = actor.Value, CorrelationId = Guid.NewGuid() };
		var request = new EditJobNodeRequest {
			Context = context,
			NodeId = new(NodeId),
			Description = Input.Description,
			WriteUp = Input.WriteUp,
			OwnerUserId = Input.OwnerUserId.HasValue ? new AppUserId(Input.OwnerUserId.Value) : null,
			ExpectedDurationHours = Input.ExpectedDurationHours,
			ExpectedCost = Input.ExpectedCost.HasValue ? new Money(Input.ExpectedCost.Value) : null,
			NeededStart = Input.NeededStart.HasValue ? Instant.FromDateTimeOffset(Input.NeededStart.Value) : null,
			NeededFinish = Input.NeededFinish.HasValue ? Instant.FromDateTimeOffset(Input.NeededFinish.Value) : null,
			Priority = Input.Priority,
			Version = OriginalVersion,
		};

		try {
			_ = await jobTrackClient.Jobs.EditAsync(request, cancellationToken);
			return RedirectToPage("/Jobs/Browse", new { nodeId = NodeId });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node no longer exists.";
			return Page();
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this node since the form was loaded. " +
						   "The latest values are shown below — try again.";
			await LoadCurrentNodeAsync(actor.Value, cancellationToken);
			await LoadOwnerOptionsAsync(actor.Value, cancellationToken);
			if (CurrentNode is { } refreshed) {
				OriginalVersion = refreshed.Node.Version;
			}

			return Page();
		}
	}

	private async Task LoadCurrentNodeAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = new JobNodeId(NodeId) }, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
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

	public sealed class EditInput
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
