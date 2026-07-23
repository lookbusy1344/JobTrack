namespace JobTrack.Web.Pages.Jobs;

using System.Globalization;
using Abstractions;
using Application;
using Domain.Hierarchy;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     Dependency editing (plan §8.5 slice 5, spec §6): shows every prerequisite edge touching a
///     node, in either direction, lets the actor search for other job nodes by title, and add or
///     remove an edge. Carries no page-level authorization policy —
///     <see cref="Domain.Authorization.JobNodeAccessPolicy" /> is re-evaluated by the command itself
///     against both endpoints of the edge, so an unauthorized attempt is denied by the command, not
///     the page.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class PrerequisitesModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	public enum PrerequisiteSelection
	{
		None,
		Requires,
		RequiredBy,
	}

	[BindProperty(SupportsGet = true)] public long NodeId { get; init; }

	/// <summary>
	///     The case-insensitive substring the actor searched for when looking up other job
	///     nodes to link (see <see cref="SearchResults" />). Round-tripped through both the search
	///     form (GET) and the "Add selected" form (POST) so the match table stays populated after an
	///     add.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public string? SearchText { get; init; }

	[BindProperty] public AddSelectionInput Input { get; set; } = new();

	public JobNodeDetailResult? CurrentNode { get; private set; }

	public EquatableArray<PrerequisiteEdge> Requires { get; private set; } = [];

	public EquatableArray<PrerequisiteEdge> RequiredBy { get; private set; } = [];

	/// <summary>
	///     Descriptions for every node named by <see cref="Requires" />/<see cref="RequiredBy" />, keyed by
	///     id, so the view can render a link instead of a bare id. An id absent here (should not happen
	///     for a live edge, but the lookup is defensive per <see cref="GetJobSummariesRequest" />) falls
	///     back to the bare id in the view.
	/// </summary>
	public IReadOnlyDictionary<JobNodeId, JobNodeSummaryResult> NodeSummariesById { get; private set; } =
		new Dictionary<JobNodeId, JobNodeSummaryResult>();

	/// <summary>
	///     Job nodes matching <see cref="SearchText" /> (the current node itself excluded), for
	///     the "Add a dependency" table. Empty when <see cref="IsSearch" /> is <see langword="false" />.
	/// </summary>
	public EquatableArray<JobNodeSummaryResult> SearchResults { get; private set; } = [];

	/// <summary>
	///     Ids already linked as a prerequisite of the current node — the search table disables
	///     the "Requires" checkbox for these rather than let the actor submit a duplicate edge.
	/// </summary>
	public IReadOnlySet<JobNodeId> ExistingRequiresIds { get; private set; } = new HashSet<JobNodeId>();

	/// <summary>
	///     Ids already linked as depending on the current node — the search table disables the
	///     "Required by" checkbox for these rather than let the actor submit a duplicate edge.
	/// </summary>
	public IReadOnlySet<JobNodeId> ExistingRequiredByIds { get; private set; } = new HashSet<JobNodeId>();

	public bool IsSearch => !string.IsNullOrWhiteSpace(SearchText);

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	/// <summary>
	///     <paramref name="nodeId" />'s display title, or the bare id when no summary was resolved —
	///     the accessible name each row's icon-only Remove button carries, so the control names the
	///     edge it removes rather than repeating "Remove" once per row.
	/// </summary>
	public string DescribeNode(JobNodeId nodeId) =>
		NodeSummariesById.TryGetValue(nodeId, out var summary)
			? JobNodeDisplay.Title(summary)
			: $"job {nodeId.Value.ToString(CultureInfo.InvariantCulture)}";

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	/// <summary>
	///     Adds every dependency edge the actor checked in the search results table — the
	///     checked "Requires" boxes become edges where the current node depends on the other job, and
	///     the checked "Required by" boxes become edges where the other job depends on the current
	///     node. Each edge is submitted as its own <see cref="AddPrerequisiteRequest" />: a duplicate or
	///     cycle-forming edge fails that one item without discarding the rest of the selection, since
	///     the actor may have checked several unrelated jobs in one pass.
	/// </summary>
	public async Task<IActionResult> OnPostAddSelectedAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var requiresIds = Input.Selections
			.Where(selection => selection.Value == PrerequisiteSelection.Requires)
			.Select(selection => new JobNodeId(selection.Key))
			.ToArray();
		var requiredByIds = Input.Selections
			.Where(selection => selection.Value == PrerequisiteSelection.RequiredBy)
			.Select(selection => new JobNodeId(selection.Key))
			.ToArray();

		if (requiresIds.Length == 0 && requiredByIds.Length == 0) {
			ErrorMessage = "Select at least one job to link before adding.";
			return RedirectToPage(CurrentRouteValues());
		}

		var failures = new List<string>();
		var addedCount = 0;

		try {
			foreach (var otherId in requiresIds) {
				addedCount += await AddOneAsync(actor.Value, otherId, new(NodeId), failures, cancellationToken);
			}

			foreach (var otherId in requiredByIds) {
				addedCount += await AddOneAsync(actor.Value, new(NodeId), otherId, failures, cancellationToken);
			}
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}

		if (addedCount > 0) {
			SuccessMessage = addedCount == 1 ? "Dependency added." : $"{addedCount} dependencies added.";
		}

		if (failures.Count > 0) {
			ErrorMessage = string.Join(" ", failures);
		}

		return RedirectToPage(CurrentRouteValues());
	}

	private async Task<int> AddOneAsync(
		AppUserId actor, JobNodeId requiredJobId, JobNodeId dependentJobId, List<string> failures, CancellationToken cancellationToken)
	{
		try {
			await jobTrackClient.Jobs.AddPrerequisiteAsync(
				new() {
					Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() },
					RequiredJobId = requiredJobId,
					DependentJobId = dependentJobId,
				}, cancellationToken);
			return 1;
		}
		catch (EntityNotFoundException) {
			failures.Add("One of those job nodes no longer exists.");
			return 0;
		}
		catch (InvariantViolationException ex) {
			failures.Add(ex.Message);
			return 0;
		}
	}

	public async Task<IActionResult> OnPostRemoveAsync(long requiredJobId, long dependentJobId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			await jobTrackClient.Jobs.RemovePrerequisiteAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					RequiredJobId = new(requiredJobId),
					DependentJobId = new(dependentJobId),
				}, cancellationToken);
			SuccessMessage = "Prerequisite removed.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That prerequisite edge does not exist.";
		}

		return RedirectToPage(CurrentRouteValues());
	}

	/// <summary>
	///     The page's own browsing context, replayed on the redirect every mutating handler ends with
	///     so the reloaded GET lands back on the same node and search text.
	/// </summary>
	private object CurrentRouteValues() => new { nodeId = NodeId, searchText = SearchText };

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		try {
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = new JobNodeId(NodeId) }, cancellationToken);

			var edges = await jobTrackClient.Query.GetPrerequisitesAsync(new() { Context = context, NodeId = new(NodeId) }, cancellationToken);

			Requires = [.. edges.Where(e => e.DependentJobId.Value == NodeId)];
			RequiredBy = [.. edges.Where(e => e.RequiredJobId.Value == NodeId)];

			var farEndIds = edges
				.Select(e => e.RequiredJobId.Value == NodeId ? e.DependentJobId : e.RequiredJobId)
				.Distinct()
				.ToArray();
			if (farEndIds.Length > 0) {
				var summaries =
					await jobTrackClient.Query.GetJobSummariesAsync(new() { Context = context, NodeIds = [.. farEndIds] }, cancellationToken);
				NodeSummariesById = summaries.ToDictionary(s => s.Id);
			}

			ExistingRequiresIds = Requires.Select(e => e.RequiredJobId).ToHashSet();
			ExistingRequiredByIds = RequiredBy.Select(e => e.DependentJobId).ToHashSet();

			if (IsSearch) {
				var matches = await jobTrackClient.Query.SearchJobNodesAsync(new() { Context = context, SearchText = SearchText! },
					cancellationToken);
				SearchResults = [.. matches.Where(m => m.Id.Value != NodeId)];
			}
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

	public sealed class AddSelectionInput
	{
		/// <summary>
		///     The radio choice made for each unlinked search result row, keyed by job node id —
		///     a row can be selected as a prerequisite for the current node, as required by the current
		///     node, or left at <see cref="PrerequisiteSelection.None" />. A single radio group per row
		///     (rather than the earlier two independent checkboxes) makes the two directions mutually
		///     exclusive, since a job can never be both a prerequisite of and dependent on the same other
		///     job.
		/// </summary>
		public Dictionary<long, PrerequisiteSelection> Selections { get; set; } = [];
	}
}
