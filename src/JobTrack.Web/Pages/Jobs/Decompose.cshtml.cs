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
///     Atomically decomposes a currently-worked leaf into a branch (plan §8.5 slice 3, spec §3.5): the
///     existing work becomes one child unchanged, and up to <see cref="MaxNewChildSlots" /> newly
///     identified jobs become siblings of it. Recovers from a stale <see cref="ConcurrencyConflictException" />
///     the same way <see cref="EditModel" /> and <see cref="MoveModel" /> do — reloading the current
///     version and returning to the input form with the user's attempted values intact rather than
///     discarding them.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class DecomposeModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver) : PageModel
{
	private const int MaxNewChildSlots = 5;

	[BindProperty(SupportsGet = true)] public long LeafNodeId { get; init; }

	[BindProperty] public long OriginalVersion { get; set; }

	[BindProperty] public DecomposeInput Input { get; set; } = new();

	public JobNodeDetailResult? CurrentNode { get; private set; }

	/// <summary>
	///     The leaf's current work state — achievement and the sessions still running — so the form can
	///     say exactly what the existing-work child is about to inherit, rather than leaving the reader
	///     to discover after the fact that a running clock moved onto a node they did not know would be
	///     created.
	/// </summary>
	public LeafWorkPageResult? WorkPage { get; private set; }

	/// <summary>
	///     Whether to offer the form at all. False withdraws it while leaving the node's name and its
	///     link back to Browse in place, so a refusal still tells the reader which job it is about.
	/// </summary>
	public bool CanDecompose { get; private set; } = true;

	public string? ErrorMessage { get; private set; }

	public List<SelectListItem> OwnerOptions { get; private set; } = [];

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadCurrentNodeAsync(actor.Value, cancellationToken);
		await LoadOwnerOptionsAsync(actor.Value, cancellationToken);
		var node = CurrentNode;
		if (node is not null) {
			OriginalVersion = node.Node.Version;
			if (node.Node.HasChildren) {
				ErrorMessage = "Only a leaf holding existing work can be decomposed.";
				CurrentNode = null;
			} else if (!node.Node.HasLeafWork) {
				// The command refuses this outright ("leaf-work-not-attached"), so withdraw the form
				// rather than collecting the exception at save time: there is no work here for the
				// existing-work child to inherit, and creating children is the plain Create page's job.
				// Only the form goes -- the node keeps its name and its link back to Browse (ADR 0044),
				// which is exactly what a reader sent here by mistake needs next.
				ErrorMessage = "This job has no recorded work to carry over, so there is nothing to decompose. "
							   + "Add children to it directly instead.";
				CanDecompose = false;
			}
		}

		// Every new child defaults to the decomposed node's own owner, matching the existing-work child,
		// which inherits that owner in the command itself: a decomposition splits one person's job into
		// the pieces it turned out to need, so leaving the pieces in the unassigned pool is the rarer
		// intent, not the default one. Still just a default -- each slot can be reassigned, or set back
		// to Unassigned, before saving. A node that is itself unassigned defaults its children the same
		// way it is: unassigned.
		var defaultOwnerUserId = CurrentNode?.Node.OwnerUserId?.Value;
		for (var i = Input.NewChildren.Count; i < MaxNewChildSlots; ++i) {
			Input.NewChildren.Add(new() { OwnerUserId = defaultOwnerUserId });
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

		var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
		var newChildren = new List<NewChildJobSpec>();
		foreach (var child in Input.NewChildren.Where(child => !string.IsNullOrWhiteSpace(child.Description))) {
			if (!BackdateInstant.TryParseOptional(child.NeededStart, zone, out var childNeededStart)
				|| !BackdateInstant.TryParseOptional(child.NeededFinish, zone, out var childNeededFinish)) {
				ErrorMessage = "Enter a valid date and time for each new child.";
				return Page();
			}

			newChildren.Add(new() {
				Description = child.Description!,
				WriteUp = child.WriteUp,
				OwnerUserId = child.OwnerUserId.HasValue ? new AppUserId(child.OwnerUserId.Value) : null,
				Priority = child.Priority,
				ExpectedDurationHours = child.ExpectedDurationHours,
				ExpectedCost = child.ExpectedCost.HasValue ? new Money(child.ExpectedCost.Value) : null,
				NeededStart = childNeededStart,
				NeededFinish = childNeededFinish,
			});
		}

		var context = new CommandContext { Actor = actor.Value, CorrelationId = Guid.NewGuid() };
		var request = new DecomposeWorkedLeafRequest {
			Context = context,
			LeafNodeId = new(LeafNodeId),
			Version = OriginalVersion,
			BranchDescription = Input.BranchDescription,
			ExistingWorkDescription = Input.ExistingWorkDescription,
			NewChildren = [.. newChildren],
		};

		try {
			var result = await jobTrackClient.Jobs.DecomposeWorkedLeafAsync(request, cancellationToken);
			return RedirectToPage("/Jobs/Browse", new { nodeId = result.BranchId.Value });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node no longer exists.";
			return Page();
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = $"This leaf cannot be decomposed: {ex.Message}";
			await LoadCurrentNodeAsync(actor.Value, cancellationToken);
			await LoadOwnerOptionsAsync(actor.Value, cancellationToken);
			return Page();
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this node since the form was loaded. " +
						   "The latest version is shown below — try again.";
			await LoadCurrentNodeAsync(actor.Value, cancellationToken);
			await LoadOwnerOptionsAsync(actor.Value, cancellationToken);
			var refreshed = CurrentNode;
			if (refreshed is not null) {
				OriginalVersion = refreshed.Node.Version;
			}

			return Page();
		}
	}

	private async Task LoadCurrentNodeAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = new JobNodeId(LeafNodeId) }, cancellationToken);
			WorkPage = await jobTrackClient.Query.GetLeafWorkPageAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, JobNodeId = new(LeafNodeId) }, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node no longer exists.";
		}
	}

	private async Task LoadOwnerOptionsAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		var directory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() } },
			cancellationToken);
		OwnerOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Unassigned", string.Empty));
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class DecomposeInput
	{
		[Required] public string BranchDescription { get; set; } = string.Empty;

		[Required] public string ExistingWorkDescription { get; set; } = string.Empty;

		public List<NewChildSlotInput> NewChildren { get; set; } = [];
	}

	public sealed class NewChildSlotInput
	{
		public string? Description { get; set; }

		[Display(Name = "Write-up")] public string? WriteUp { get; set; }

		[Display(Name = "Owner")] public long? OwnerUserId { get; set; }

		public Priority Priority { get; set; } = Priority.Medium;

		[Display(Name = "Expected duration (hours)")]
		public decimal? ExpectedDurationHours { get; set; }

		[Display(Name = "Expected cost")] public decimal? ExpectedCost { get; set; }

		[Display(Name = "Needed start")] public string? NeededStart { get; set; }

		[Display(Name = "Needed finish")] public string? NeededFinish { get; set; }
	}
}
