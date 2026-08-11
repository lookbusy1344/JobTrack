namespace JobTrack.Web.Pages.Requests;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodaTime;

/// <summary>
///     A single request's requester-safe detail: status, read-only subtree, and the notes thread (ADR
///     0034, plan §7/§8). Reachable by the request's own requester or by staff triaging it — coarse
///     admission is <see cref="JobTrackPolicyNames.RequestDetailAccess" />, the same combined policy the
///     API endpoints use; the authoritative per-request check happens inside
///     <see cref="IJobTrackClient.Requests" /> itself. ADR 0054 exposes only aggregate allocated
///     duration; owner, rates, individual sessions, schedules, and audit fields remain on the staff
///     job surfaces.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.RequestDetailAccess)]
public sealed class DetailsModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver)
	: PageModel
{
	[BindProperty] public AddNoteInput NoteInput { get; set; } = new();

	[BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

	public string BackUrl { get; private set; } = "/";

	public string? SafeReturnUrl => ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : null;

	public JobRequestDetailResult? Detail { get; private set; }

	public IReadOnlyList<SubtreeRow> OrderedSubtree { get; private set; } = [];

	/// <summary>
	///     The requester as "display name (username)" — the one rendering every staff surface uses for a
	///     person (<see cref="EmployeeDirectoryDisplay" />), so the two names read as one fact rather
	///     than as two adjacent fields.
	/// </summary>
	public string RequesterLabel => Detail is null
		? string.Empty
		: EmployeeDirectoryDisplay.Format(Detail.RequesterDisplayName, Detail.RequesterUserName);

	/// <summary>The signed-in actor's own time zone, for formatting every timestamp on this page (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	public bool CanAcknowledge { get; private set; }

	[TempData] public string? ErrorMessage { get; set; }

	public async Task<IActionResult> OnGetAsync(long id, CancellationToken cancellationToken)
	{
		CanAcknowledge = IsStaff();
		return await LoadAsync(id, cancellationToken);
	}

	public async Task<IActionResult> OnPostAddNoteAsync(long id, CancellationToken cancellationToken)
	{
		CanAcknowledge = IsStaff();
		ModelState.Clear();
		if (!TryValidateModel(NoteInput, nameof(NoteInput))) {
			return await LoadAsync(id, cancellationToken);
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Requests.AddNoteAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				NodeId = new(id),
				Content = NoteInput.Content,
				VisibleToRequester = NoteInput.VisibleToRequester,
			}, cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			return NotFound();
		}

		return RedirectToPage(new { id, returnUrl = SafeReturnUrl });
	}

	public async Task<IActionResult> OnPostAcknowledgeAsync(long id, long version, CancellationToken cancellationToken)
	{
		CanAcknowledge = IsStaff();

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Requests.AcknowledgeAsync(
				new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, NodeId = new(id), Version = version },
				cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			return NotFound();
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "This request was changed by someone else. Reload and try again.";
		}

		return RedirectToPage(new { id, returnUrl = SafeReturnUrl });
	}

	private async Task<IActionResult> LoadAsync(long id, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);

		try {
			Detail = await jobTrackClient.Requests.GetDetailAsync(
				new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, NodeId = new(id) }, cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			return NotFound();
		}
		catch (InvariantViolationException) {
			return NotFound();
		}

		OrderedSubtree = BuildOrderedSubtree(Detail);
		BackUrl = SafeReturnUrl
				  ?? (User.IsInRole(EmployeeRoleNames.Requester)
					  ? Url.Page("/Requests/Index")
					  : Url.Page("/Jobs/Browse", new { nodeId = id }))
				  ?? "/";
		return Page();
	}

	/// <summary>Root-first, depth-ordered walk of the flat subtree, for indented rendering.</summary>
	private static List<SubtreeRow> BuildOrderedSubtree(JobRequestDetailResult detail)
	{
		var childrenByParentId = detail.Subtree.Where(n => n.ParentId is not null)
			.GroupBy(n => n.ParentId!.Value)
			.ToDictionary(g => g.Key, g => g.ToArray());
		var root = detail.Subtree.First(n => n.JobNodeId == detail.JobNodeId);

		var rows = new List<SubtreeRow>();
		var pending = new Stack<(RequesterSubtreeNodeResult Node, int Depth)>();
		pending.Push((root, 0));
		while (pending.Count > 0) {
			var (node, depth) = pending.Pop();
			var kind = childrenByParentId.ContainsKey(node.JobNodeId) ? NodeKind.Branch : NodeKind.Leaf;
			rows.Add(new(node, depth, kind));

			if (childrenByParentId.TryGetValue(node.JobNodeId, out var children)) {
				foreach (var child in children.Reverse()) {
					pending.Push((child, depth + 1));
				}
			}
		}

		return rows;
	}

	private bool IsStaff() =>
		User.IsInRole(EmployeeRoleNames.Administrator) || User.IsInRole(EmployeeRoleNames.JobManager) || User.IsInRole(EmployeeRoleNames.Worker);

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class AddNoteInput
	{
		[Required][MaxCodePointLength(4000)] public string Content { get; init; } = string.Empty;

		public bool VisibleToRequester { get; init; }
	}

	/// <summary>One subtree node paired with its indentation depth for rendering (<see cref="BuildOrderedSubtree" />).</summary>
	public sealed record SubtreeRow(RequesterSubtreeNodeResult Node, int Depth, NodeKind Kind);
}
