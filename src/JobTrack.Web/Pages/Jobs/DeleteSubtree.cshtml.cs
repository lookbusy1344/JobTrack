namespace JobTrack.Web.Pages.Jobs;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Domain.Costing;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodaTime;

/// <summary>
///     Recursively deletes a whole subtree, or archives it instead (ADR 0061). Administrator-only —
///     both the page policy and <see cref="IJobCommands.DeleteSubtreeAsync" /> itself enforce that, the
///     latter being authoritative. The confirmation lists exactly what would be destroyed via
///     <see cref="IJobQueries.GetSubtreeImpactAsync" />, recomputed on every GET and again inside the
///     deleting transaction, so a stale manifest can never authorize a larger deletion than the one
///     shown. Single-node deletion keeps its own page (<see cref="DeleteModel" />), which never
///     cascades; this one is reached from Browse only for a node that has children.
/// </summary>
[Authorize(Policy = EmployeeRoleNames.Administrator)]
public sealed partial class DeleteSubtreeModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IClock clock,
	ILogger<DeleteSubtreeModel> logger) : PageModel
{
	[BindProperty(SupportsGet = true)] public long NodeId { get; init; }

	[BindProperty] public long OriginalVersion { get; set; }

	[BindProperty] public DeleteSubtreeInput Input { get; set; } = new();

	public JobNodeDetailResult? CurrentNode { get; private set; }

	public SubtreeImpactResult? Impact { get; private set; }

	/// <summary>
	///     Each node's rolled-up cost, keyed by node id, and the matching allocated durations. Empty
	///     when costs could not be shown — see <see cref="CostUnavailableReason" />. A node absent from
	///     the dictionary is one this actor may not see the cost of (ADR 0040/0042), rendered blank
	///     exactly as every other cost-hidden row is, never as an error.
	/// </summary>
	public EquatableDictionary<JobNodeId, Money> NodeCosts { get; private set; }

	/// <inheritdoc cref="NodeCosts" />
	public EquatableDictionary<JobNodeId, AllocatedDuration> NodeAllocatedDurations { get; private set; }

	/// <summary>The whole subtree's rolled-up cost — the subtree root's own entry in <see cref="NodeCosts" />.</summary>
	public Money? TotalCost { get; private set; }

	/// <inheritdoc cref="TotalCost" />
	public AllocatedDuration? TotalAllocatedDuration { get; private set; }

	/// <summary>
	///     Why the cost breakdown is absent, or <see langword="null" /> when it is present. Cost is
	///     supplementary context on a destructive confirmation, so a missing rate or a cost-permission
	///     refusal degrades this one panel rather than blocking the deletion the page exists to confirm.
	/// </summary>
	public string? CostUnavailableReason { get; private set; }

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);

		return Page();
	}

	public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
	{
		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		var node = CurrentNode;
		if (node is null || !ModelState.IsValid) {
			return Page();
		}

		var parentId = node.Node.ParentId;

		var correlationId = Guid.NewGuid();

		try {
			_ = await jobTrackClient.Jobs.DeleteSubtreeAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = correlationId },
				RootId = new(NodeId),
				Version = OriginalVersion,
				Reason = Input.Reason ?? string.Empty,
			}, cancellationToken);

			return RedirectToPage("/Jobs/Browse", parentId.HasValue ? new { nodeId = parentId.Value.Value } : null);
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "Deleting a whole subtree requires the Administrator role.";
			return Page();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node no longer exists.";
			return Page();
		}
		catch (InvariantViolationException ex) when (ex.ConstraintId == "subtree-delete-reason-required") {
			ErrorMessage = "Deleting a subtree requires a reason.";
			return Page();
		}
		catch (InvariantViolationException ex) {
			// Logged for the same reason the single-node page logs it (ADR 0068): a refusal ends in a
			// 200 the request log cannot be told apart from a success, and the catch-all categories
			// name no table -- only the exception's own provider error does.
			LogSubtreeDeleteRefused(logger, correlationId, NodeId, ex.ConstraintId, ex);
			ErrorMessage = $"This subtree cannot be deleted: {ex.Message}";
			return Page();
		}
		catch (ConcurrencyConflictException ex) {
			PageFailureLogging.LogConcurrencyConflict(logger, correlationId, nameof(DeleteSubtreeModel), ex);
			return await ReloadAfterConflictAsync(actor.Value, cancellationToken);
		}
	}

	public async Task<IActionResult> OnPostArchiveAsync(CancellationToken cancellationToken)
	{
		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		if (CurrentNode is null) {
			return Page();
		}

		var correlationId = Guid.NewGuid();

		try {
			_ = await jobTrackClient.Jobs.ArchiveSubtreeAsync(
				new() { Context = new() { Actor = actor.Value, CorrelationId = correlationId }, RootId = new(NodeId), Version = OriginalVersion },
				cancellationToken);

			return RedirectToPage("/Jobs/Browse", new { nodeId = NodeId });
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "Archiving a whole subtree requires the Administrator role.";
			return Page();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node no longer exists.";
			return Page();
		}
		catch (InvariantViolationException ex) when (ex.ConstraintId == "leaf-closure-active-sessions") {
			ErrorMessage = "A session is still running somewhere in this subtree; pause or finish it before archiving.";
			return Page();
		}
		catch (ConcurrencyConflictException ex) {
			PageFailureLogging.LogConcurrencyConflict(logger, correlationId, nameof(DeleteSubtreeModel), ex);
			return await ReloadAfterConflictAsync(actor.Value, cancellationToken);
		}
	}

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "job_subtree_delete_refused correlation_id={CorrelationId} node_id={NodeId} constraint={ConstraintId}")]
	private static partial void LogSubtreeDeleteRefused(
		ILogger logger, Guid correlationId, long nodeId, string constraintId, Exception exception);

	private async Task<IActionResult> ReloadAfterConflictAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		ErrorMessage = "Someone else changed this subtree since the form was loaded. " +
					   "The latest contents are shown below — review and try again.";
		await LoadAsync(actor, cancellationToken);
		var refreshed = CurrentNode;
		if (refreshed is not null) {
			OriginalVersion = refreshed.Node.Version;
		}

		return Page();
	}

	/// <summary>
	///     Loads the node and its impact manifest together. A node with no children is redirected in the
	///     markup toward the single-node page rather than handled here: this page exists for the
	///     cascading case, and offering it for a lone leaf would present a subtree warning for a
	///     one-row deletion.
	/// </summary>
	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = new JobNodeId(NodeId) },
				cancellationToken);
			OriginalVersion = OriginalVersion == 0 ? CurrentNode.Node.Version : OriginalVersion;

			Impact = await jobTrackClient.Query.GetSubtreeImpactAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, RootId = new(NodeId) },
				cancellationToken);

			await LoadCostsAsync(actor, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
			CurrentNode = null;
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "Viewing this subtree's deletion impact requires the Administrator role.";
			CurrentNode = null;
		}
	}

	/// <summary>
	///     Loads the subtree's rolled-up cost breakdown in one call — <c>GetHierarchyTotalsAsync</c>
	///     already returns a per-node dictionary plus, in the root's own entry, the whole-subtree total,
	///     so there is no per-row round trip. Every failure here is caught and turned into a note on the
	///     panel: cost is context for the decision, not the decision itself, and an administrator must
	///     still be able to delete a subtree whose rates no longer resolve.
	/// </summary>
	private async Task LoadCostsAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			var totals = await jobTrackClient.Costs.GetHierarchyTotalsAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = new(NodeId), AsOf = clock.GetCurrentInstant() },
				cancellationToken);

			NodeCosts = totals.DisplayedCosts;
			NodeAllocatedDurations = totals.AllocatedDurations;
			if (totals.DisplayedCosts.TryGetValue(new(NodeId), out var total)) {
				TotalCost = total;
			}

			if (totals.AllocatedDurations.TryGetValue(new(NodeId), out var totalDuration)) {
				TotalAllocatedDuration = totalDuration;
			}
		}
		catch (AuthorizationDeniedException) {
			CostUnavailableReason = "You do not have permission to view costs for this subtree.";
		}
		catch (MissingRateException) {
			CostUnavailableReason = "Costs cannot be shown: no rate resolves for at least one session in this subtree.";
		}
		catch (ArgumentOutOfRangeException) {
			CostUnavailableReason = "This subtree is too large to cost in one pass; the deletion figures above still apply.";
		}
	}

	public sealed class DeleteSubtreeInput
	{
		/// <summary>
		///     Always required for a subtree deletion (ADR 0061), unlike the single-node page's
		///     conditional reason — the server enforces it too, and is authoritative.
		/// </summary>
		[Required(ErrorMessage = "A reason is required to delete a subtree.")]
		public string? Reason { get; set; }
	}
}
