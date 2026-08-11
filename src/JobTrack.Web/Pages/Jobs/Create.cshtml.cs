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

/// <summary>
///     Creates a child node under a chosen parent (plan §8.5 slice 3). Carries no page-level
///     authorization policy — <see cref="Domain.Authorization.JobNodeAccessPolicy" /> is re-evaluated
///     against the parent's subtree inside the command itself (plan §8.3), so any authenticated
///     employee may reach this page and let the command's <see cref="AuthorizationDeniedException" />
///     deny it.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class CreateModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver) : PageModel
{
	private const string ParentHasLeafWorkMessage =
		"This parent already has work attached. Create children only under a node without leaf work.";

	private const string BlockedMessage =
		"This job's prerequisites are not satisfied, so work cannot begin on it yet. Create it without a worker "
		+ "and start the session once it is ready.";

	/// <summary>
	///     The structural rejection <c>IJobCommands.AddChildAsync</c> reports for a parent that already
	///     holds leaf work; every other constraint it can raise is about the chosen owner or worker, and
	///     <see cref="WorkSessionFailureDisplay" /> already has the sentence for those.
	/// </summary>
	private const string WriteRejectedConstraintId = "job-node-write-rejected";

	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty(SupportsGet = true)] public long ParentId { get; init; }

	[BindProperty] public CreateInput Input { get; set; } = new();

	public JobNodeDetailResult? Parent { get; private set; }

	public string? ErrorMessage { get; private set; }

	public List<SelectListItem> OwnerOptions { get; private set; } = [];

	/// <summary>
	///     The same workflow-employee directory as <see cref="OwnerOptions" />, headed by "None" — the
	///     default, since a new node is just as often a branch-to-be or a leaf whose sessions start later.
	/// </summary>
	public List<SelectListItem> BeginWorkOptions { get; private set; } = [];

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

		var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
		if (!BackdateInstant.TryParseOptional(Input.NeededStart, zone, out var neededStart)
			|| !BackdateInstant.TryParseOptional(Input.NeededFinish, zone, out var neededFinish)) {
			ErrorMessage = "Enter a valid date and time.";
			return Page();
		}

		var request = new CreateJobNodeRequest {
			Context = context,
			ParentId = new(ParentId),
			Description = Input.Description,
			WriteUp = Input.WriteUp,
			OwnerUserId = Input.OwnerUserId.HasValue ? new AppUserId(Input.OwnerUserId.Value) : null,
			ExpectedDurationHours = Input.ExpectedDurationHours,
			ExpectedCost = Input.ExpectedCost.HasValue ? new Money(Input.ExpectedCost.Value) : null,
			NeededStart = neededStart,
			NeededFinish = neededFinish,
			Priority = Input.Priority,
			BeginWork = Input.BeginWorkForUserId.HasValue
				? new CreateJobNodeWorkSpec { WorkedByUserId = new(Input.BeginWorkForUserId.Value) }
				: null,
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
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.ConstraintId == WriteRejectedConstraintId
				? ParentHasLeafWorkMessage
				: WorkSessionFailureDisplay.Describe(ex);
			await LoadParentAsync(context.Actor, cancellationToken);
			await LoadOwnerOptionsAsync(context.Actor, cancellationToken);
			return Page();
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = BlockedMessage;
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
		BeginWorkOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("None", string.Empty));
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class CreateInput
	{
		[Required] public string Description { get; set; } = string.Empty;

		[Display(Name = "Write-up")] public string? WriteUp { get; set; }

		[Display(Name = "Owner")] public long? OwnerUserId { get; set; }

		/// <summary>
		///     The employee whose session opens on the new node as it is created, or <see langword="null" />
		///     ("None", the default) to create it with no work under way.
		/// </summary>
		[Display(Name = "Begin work for")]
		public long? BeginWorkForUserId { get; set; }

		[Display(Name = "Expected duration (hours)")]
		public decimal? ExpectedDurationHours { get; set; }

		[Display(Name = "Expected cost")] public decimal? ExpectedCost { get; set; }

		[Display(Name = "Start by")] public string? NeededStart { get; set; }

		[Display(Name = "Deadline")] public string? NeededFinish { get; set; }

		[Required] public Priority Priority { get; set; }
	}
}
