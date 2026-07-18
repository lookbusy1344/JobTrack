namespace JobTrack.Web.Pages.Jobs;

using System.Globalization;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

/// <summary>
///     A leaf's own work-session detail (plan §8.5 slice 4): a deep-linkable page showing the same
///     <c>_LeafWorkSessions</c> panel <c>Browse</c> shows inline for a leaf, plus <c>CorrectSession</c>'s
///     post-redirect target. "Pause" and "resume" are UI terms only — pause posts to the same handler as
///     finish, and resume posts to the same handler as start (spec §4.4); there is no separate
///     resume/pause command. Historical correction lives on the separate <see cref="CorrectSessionModel" />
///     page. Carries no page-level authorization policy — the commands themselves re-evaluate
///     <see cref="Domain.Authorization.WorkSessionAccessPolicy" /> per call, so an unauthorized attempt is
///     denied by the command, not the page.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class WorkModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private EquatableArray<EmployeeDirectoryEntry> _employeeDirectory = [];

	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty(SupportsGet = true)] public long LeafNodeId { get; init; }

	[BindProperty(SupportsGet = true)] public long? WorkedByUserId { get; init; }

	public JobNodeDetailResult? CurrentNode { get; private set; }

	public LeafWorkSessionsPanelModel? Panel { get; private set; }

	public string? ErrorMessage { get; private set; }

	public string? SuccessMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, WorkedByUserId is { } id ? new AppUserId(id) : null, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostStartAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var workedByUserId = WorkedByUserId is { } id ? new(id) : actor.Value;

		try {
			_ = await jobTrackClient.Work.StartWorkAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					JobNodeId = new(LeafNodeId),
					WorkedByUserId = workedByUserId,
				}, cancellationToken);
			SuccessMessage = "Work started.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.ConstraintId == "work-session-already-active"
				? "This worker already has an active session for this leaf."
				: ex.Message;
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied.";
		}

		await LoadAsync(actor.Value, WorkedByUserId is { } filterId ? new AppUserId(filterId) : null, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostFinishAsync(long sessionId, long version, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Work.FinishSessionAsync(
				new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, SessionId = new(sessionId), Version = version },
				cancellationToken);
			SuccessMessage = "Session finished.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That session does not exist.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this session since the page was loaded. The list below is refreshed.";
		}

		await LoadAsync(actor.Value, WorkedByUserId is { } id ? new AppUserId(id) : null, cancellationToken);
		return Page();
	}

	private async Task LoadAsync(AppUserId actor, AppUserId? workedByUserId, CancellationToken cancellationToken)
	{
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		_employeeDirectory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);
		_employeeDirectoryById = _employeeDirectory.ToDictionary(entry => entry.Id);

		try {
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = new JobNodeId(LeafNodeId) },
				cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
			return;
		}

		try {
			var sessions = await jobTrackClient.Query.GetLeafSessionsAsync(
				new() { Context = context, LeafWorkId = new(LeafNodeId), WorkedByUserId = workedByUserId }, cancellationToken);

			Panel = new() {
				LeafNodeId = LeafNodeId,
				DisplayedWorkedByUserId = workedByUserId?.Value,
				DisplayedWorkedByName = workedByUserId is { } filtered
					? EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, filtered.Value, "Unknown")
					: null,
				Sessions = sessions,
				EmployeeDirectoryById = _employeeDirectoryById,
				WorkedByOptions = BuildWorkerFilterOptions(workedByUserId),
				ExtraHiddenFields = new Dictionary<string, string?> { ["LeafNodeId"] = LeafNodeId.ToString(CultureInfo.InvariantCulture) },
			};
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view that worker's sessions on this leaf.";
		}
	}

	/// <summary>
	///     Worker-filter options for the sessions panel: an "Everyone" default (empty value,
	///     clearing the filter back to every worker's sessions) followed by each employee, with the
	///     currently filtered worker preselected.
	/// </summary>
	private List<SelectListItem> BuildWorkerFilterOptions(AppUserId? selectedId)
	{
		var options = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory, new SelectListItem("Everyone", string.Empty));
		if (selectedId is { } id) {
			foreach (var option in options) {
				option.Selected = option.Value == id.Value.ToString(CultureInfo.InvariantCulture);
			}
		}

		return options;
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}
}
