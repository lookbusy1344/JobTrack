namespace JobTrack.Web.Pages.Jobs;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     Re-parents a node (plan §8.5 slice 3) — the node's whole subtree moves with it. Recovers from
///     both failure modes a move uniquely risks: a stale <see cref="ConcurrencyConflictException" />
///     (someone else changed the node first) and an <see cref="InvariantViolationException" /> (the
///     destination is the node's own descendant, which would create a hierarchy cycle) — both keep the
///     user's chosen destination on screen rather than discarding it.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class MoveModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	[BindProperty(SupportsGet = true)] public long NodeId { get; init; }

	[BindProperty] public long OriginalVersion { get; set; }

	[BindProperty] public MoveInput Input { get; set; } = new();

	public JobNodeDetailResult? CurrentNode { get; private set; }

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadCurrentNodeAsync(actor.Value, cancellationToken);
		if (CurrentNode is { } node) {
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
		if (CurrentNode is null || !ModelState.IsValid) {
			return Page();
		}

		var context = new CommandContext { Actor = actor.Value, CorrelationId = Guid.NewGuid() };
		var request = new MoveJobNodeRequest {
			Context = context,
			NodeId = new(NodeId),
			NewParentId = new(Input.NewParentId),
			Version = OriginalVersion,
		};

		try {
			var result = await jobTrackClient.Jobs.MoveAsync(request, cancellationToken);
			return RedirectToPage("/Jobs/Browse", new { nodeId = result.Id.Value });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "The node or the destination no longer exists.";
			return Page();
		}
		catch (InvariantViolationException) {
			ErrorMessage = "This move would create a cycle in the job hierarchy — the destination cannot be this node's own descendant.";
			await LoadCurrentNodeAsync(actor.Value, cancellationToken);
			return Page();
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this node since the form was loaded. " +
						   "The latest version is shown below — try again.";
			await LoadCurrentNodeAsync(actor.Value, cancellationToken);
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

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class MoveInput
	{
		[Required] public long NewParentId { get; set; }
	}
}
