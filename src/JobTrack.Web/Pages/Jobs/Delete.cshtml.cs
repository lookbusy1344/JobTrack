namespace JobTrack.Web.Pages.Jobs;

using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     Physically deletes a leaf job node (ADR 0036). Never cascades a subtree: a node with children,
///     a prerequisite edge, or the permanent root is unconditionally rejected before this page's form
///     even renders. A leaf's own unused <c>LeafWork</c> (never worked) is deletable with it; a leaf
///     with real <c>WorkSession</c> history additionally requires the actor to hold the Administrator
///     role and to give a reason -- both enforced by <see cref="IJobCommands.DeleteAsync" /> itself, not
///     pre-checked here, since no query surface currently reveals "has this leaf ever been worked,
///     regardless of who worked it" without attempting the delete.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class DeleteModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	[BindProperty(SupportsGet = true)] public long NodeId { get; init; }

	[BindProperty] public long OriginalVersion { get; set; }

	[BindProperty] public DeleteInput Input { get; set; } = new();

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
			if (node.Node.HasChildren) {
				ErrorMessage = "A node with children cannot be deleted; delete or move its children first.";
				CurrentNode = null;
			} else if (node.Node.ParentId is null) {
				ErrorMessage = "The root job node cannot be deleted.";
				CurrentNode = null;
			}
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
		var parentId = CurrentNode.Node.ParentId;

		try {
			await jobTrackClient.Jobs.DeleteAsync(new() {
				Context = context,
				NodeId = new(NodeId),
				Version = OriginalVersion,
				Reason = string.IsNullOrWhiteSpace(Input.Reason) ? null : Input.Reason,
			}, cancellationToken);

			return RedirectToPage("/Jobs/Browse", parentId is { } id ? new { nodeId = id.Value } : null);
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "This node has worked session history; deleting it requires the Administrator role.";
			return Page();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node no longer exists.";
			return Page();
		}
		catch (InvariantViolationException ex) when (ex.ConstraintId == "job-node-delete-worked-leaf-reason-required") {
			ErrorMessage = "This node has worked session history; deleting it requires a reason (below).";
			return Page();
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = $"This node cannot be deleted: {ex.Message}";
			return Page();
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this node since the form was loaded. " +
						   "The latest version is shown below — review and try again.";
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

	public sealed class DeleteInput
	{
		/// <summary>
		///     Only required when the node has worked <c>WorkSession</c> history (ADR 0036);
		///     the server enforces that, not a <c>[Required]</c> here, since it does not apply to the
		///     common unused-node case.
		/// </summary>
		public string? Reason { get; set; }
	}
}
