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
///     Atomically decomposes a leaf into a branch (plan §8.5 slice 3, spec §3.5, ADR 0067): if the leaf
///     currently holds work, that work becomes one child unchanged; either way, up to
///     <see cref="MaxNewChildSlots" /> newly identified jobs become its children (siblings of the
///     existing-work child, when there is one). Recovers from a stale
///     <see cref="ConcurrencyConflictException" /> the same way <see cref="EditModel" /> and
///     <see cref="MoveModel" /> do — reloading the current version and returning to the input form with
///     the user's attempted values intact rather than discarding them.
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
				ErrorMessage = "Only a leaf can be decomposed.";
				CurrentNode = null;
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

		var hasLeafWork = CurrentNode.Node.HasLeafWork;
		if (hasLeafWork && string.IsNullOrWhiteSpace(Input.ExistingWorkDescription)) {
			ModelState.AddModelError(
				$"{nameof(Input)}.{nameof(Input.ExistingWorkDescription)}", "Enter a description for the child that inherits this work.");
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

		// A bare leaf has no existing-work child to fall back on, so it needs at least one named
		// child itself -- the command's own "job-node-decompose-requires-a-child" invariant, checked
		// here too so the reader sees the problem before losing their inputs to a round trip.
		if (!hasLeafWork && newChildren.Count == 0) {
			ErrorMessage = "Name at least one new child to decompose this job into.";
			return Page();
		}

		var context = new CommandContext { Actor = actor.Value, CorrelationId = Guid.NewGuid() };
		var request = new DecomposeWorkedLeafRequest {
			Context = context,
			LeafNodeId = new(LeafNodeId),
			Version = OriginalVersion,
			BranchDescription = Input.BranchDescription,
			ExistingWorkDescription = hasLeafWork ? Input.ExistingWorkDescription : null,
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

		/// <summary>
		///     Required only when the node being decomposed currently has <c>LeafWork</c> attached
		///     (<see cref="OnPostAsync" /> enforces this conditionally -- see ADR 0067); left blank for a
		///     bare leaf, which has no existing work to describe.
		/// </summary>
		public string? ExistingWorkDescription { get; set; }

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

		[Display(Name = "Start by")] public string? NeededStart { get; set; }

		[Display(Name = "Deadline")] public string? NeededFinish { get; set; }
	}
}
