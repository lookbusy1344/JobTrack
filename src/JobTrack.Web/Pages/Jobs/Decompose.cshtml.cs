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
///     Atomically decomposes a currently-worked leaf into a branch (plan §8.5 slice 3, spec §3.5): the
///     existing work becomes one child unchanged, and up to <see cref="MaxNewChildSlots" /> newly
///     identified jobs become siblings of it. Recovers from a stale <see cref="ConcurrencyConflictException" />
///     the same way <see cref="EditModel" /> and <see cref="MoveModel" /> do — reloading the current
///     version and returning to the input form with the user's attempted values intact rather than
///     discarding them.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class DecomposeModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private const int MaxNewChildSlots = 5;

	[BindProperty(SupportsGet = true)] public long LeafNodeId { get; init; }

	[BindProperty] public long OriginalVersion { get; set; }

	[BindProperty] public DecomposeInput Input { get; set; } = new();

	public JobNodeDetailResult? CurrentNode { get; private set; }

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
		if (CurrentNode is { } node) {
			OriginalVersion = node.Node.Version;
			if (node.Node.HasChildren) {
				ErrorMessage = "Only a leaf holding existing work can be decomposed.";
				CurrentNode = null;
			}
		}

		for (var i = Input.NewChildren.Count; i < MaxNewChildSlots; i++) {
			Input.NewChildren.Add(new());
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
		var newChildren = Input.NewChildren
			.Where(child => !string.IsNullOrWhiteSpace(child.Description))
			.Select(child => new NewChildJobSpec {
				Description = child.Description!,
				WriteUp = child.WriteUp,
				OwnerUserId = child.OwnerUserId.HasValue ? new AppUserId(child.OwnerUserId.Value) : null,
				Priority = child.Priority,
				ExpectedDurationHours = child.ExpectedDurationHours,
				ExpectedCost = child.ExpectedCost.HasValue ? new Money(child.ExpectedCost.Value) : null,
				NeededStart = child.NeededStart.HasValue ? Instant.FromDateTimeOffset(child.NeededStart.Value) : null,
				NeededFinish = child.NeededFinish.HasValue ? Instant.FromDateTimeOffset(child.NeededFinish.Value) : null,
			})
			.ToArray();

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
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = new JobNodeId(LeafNodeId) }, cancellationToken);
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

		public string? WriteUp { get; set; }

		public long? OwnerUserId { get; set; }

		public Priority Priority { get; set; } = Priority.Medium;

		public decimal? ExpectedDurationHours { get; set; }

		public decimal? ExpectedCost { get; set; }

		public DateTimeOffset? NeededStart { get; set; }

		public DateTimeOffset? NeededFinish { get; set; }
	}
}
