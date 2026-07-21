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
using NodaTime;

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
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	IClock clock) : PageModel
{
	private EquatableArray<EmployeeDirectoryEntry> _employeeDirectory = [];

	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	/// <summary>Captured once per request, per ADR 0016's "one captured instant per operation".</summary>
	public Instant Now { get; } = clock.GetCurrentInstant();

	[BindProperty(SupportsGet = true)] public long LeafNodeId { get; init; }

	[BindProperty(SupportsGet = true)] public long? WorkedByUserId { get; init; }

	public JobNodeDetailResult? CurrentNode { get; private set; }

	public LeafWorkSessionsPanelModel? Panel { get; private set; }

	/// <summary>The signed-in actor, so the toolbar can tell their own active session apart from another worker's.</summary>
	public AppUserId? CurrentActorId { get; private set; }

	/// <summary>
	///     Every active session on this leaf, never collapsed to one representative (plan §2.4) -- unlike <see cref="Panel" />'s history, which the
	///     worker filter narrows.
	/// </summary>
	public EquatableArray<WorkSessionResult> ActiveSessions { get; private set; } = [];

	/// <summary>
	///     Whether the actor may manage sessions on this leaf (<see cref="IJobQueries.GetSessionManageCapabilitiesAsync" />) -- a rendering hint for
	///     the "Start for…" disclosure and another worker's exact finish.
	/// </summary>
	public bool CanManage { get; private set; }

	/// <summary>The leaf's current achievement, when work is attached.</summary>
	public Achievement? CurrentAchievement { get; private set; }

	/// <summary>Every enabled workflow employee, for the "Start for…" worker picker.</summary>
	public IReadOnlyList<SelectListItem> StartForWorkerOptions { get; private set; } = [];

	/// <summary>The one leaf this page is for, as a <see cref="WorkRowActionsModel" /> so the toolbar shares Browse's exact start/finish/start-for logic.</summary>
	public WorkRowActionsModel ToolbarActions => new() {
		LeafNodeId = LeafNodeId,
		ViewerId = CurrentActorId ?? new AppUserId(0),
		ActiveSessions = ActiveSessions,
		CanManage = CanManage,
		Achievement = CurrentAchievement,
		IsArchived = CurrentNode?.Node.ArchivedAt is not null,
		ViewerZone = ViewerZone,
		StartHandler = "Start",
		StartNodeFieldName = "leafNodeId",
		PageStateFields = ToolbarStateFields,
		StartForWorkerOptions = StartForWorkerOptions,
		StartForLabelled = true,
	};

	/// <summary>The signed-in actor's own time zone, for formatting every timestamp on this page (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	/// <summary>
	///     The toolbar's own round-trip state — which leaf, and whose sessions are being shown — as
	///     hidden fields, so starting work (or backdating a start) returns to the same filtered view.
	/// </summary>
	public IReadOnlyDictionary<string, string?> ToolbarStateFields => new Dictionary<string, string?> {
		["LeafNodeId"] = LeafNodeId.ToString(CultureInfo.InvariantCulture),
		["WorkedByUserId"] = Panel?.DisplayedWorkedByUserId?.ToString(CultureInfo.InvariantCulture),
	};

	/// <summary>Formats a worker id for display (<see cref="EmployeeDirectoryDisplay.Describe" />), for the plural Active-column summary.</summary>
	public string DescribeWorker(AppUserId workerId) => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, workerId.Value);

	/// <summary>
	///     <see cref="ToolbarStateFields" /> plus the identity and version of <paramref name="session" />,
	///     for the forms acting on an existing session rather than on the leaf.
	/// </summary>
	public IReadOnlyDictionary<string, string?> SessionFields(WorkSessionResult session)
	{
		ArgumentNullException.ThrowIfNull(session);

		return new Dictionary<string, string?>(ToolbarStateFields) {
			["sessionId"] = session.Id.Value.ToString(CultureInfo.InvariantCulture),
			["version"] = session.Version.ToString(CultureInfo.InvariantCulture),
		};
	}

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostStartAsync(string? startedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		// Always the viewer's own one-click start -- WorkedByUserId is the Sessions history filter
		// only and must never double as a mutation target (plan §2.5 rule 4). Starting for another
		// worker goes through OnPostStartForAsync's distinct StartForUserId field.
		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(startedAt, zone, out var startedAtInstant)) {
				ErrorMessage = "Enter a valid date and time.";
				return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
			}

			_ = await jobTrackClient.Work.StartWorkAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					JobNodeId = new(LeafNodeId),
					WorkedByUserId = actor.Value,
					StartedAt = startedAtInstant,
				}, cancellationToken);
			SuccessMessage = "Session started.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied.";
		}

		return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
	}

	/// <summary>Starts a session for <paramref name="startForUserId" /> rather than the signed-in actor -- mirrors <c>Browse</c>'s <c>StartFor</c> handler.</summary>
	public async Task<IActionResult> OnPostStartForAsync(long? startForUserId, string? startedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		if (startForUserId is not long targetUserId) {
			ErrorMessage = "Choose a worker to start for.";
			return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
		}

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(startedAt, zone, out var startedAtInstant)) {
				ErrorMessage = "Enter a valid date and time.";
				return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
			}

			_ = await jobTrackClient.Work.StartWorkAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					JobNodeId = new(LeafNodeId),
					WorkedByUserId = new(targetUserId),
					StartedAt = startedAtInstant,
				}, cancellationToken);
			SuccessMessage = "Session started.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node or worker does not exist.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied.";
		}

		return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
	}

	public async Task<IActionResult> OnPostFinishAsync(
		long sessionId, long version, string? finishedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(finishedAt, zone, out var finishedAtInstant)) {
				ErrorMessage = "Enter a valid date and time.";
				return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
			}

			_ = await jobTrackClient.Work.FinishSessionAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				SessionId = new(sessionId),
				Version = version,
				FinishedAt = finishedAtInstant,
			}, cancellationToken);
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
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}

		return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
	}

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		CurrentActorId = actor;
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor, cancellationToken);
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		_employeeDirectory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);
		_employeeDirectoryById = _employeeDirectory.ToDictionary(entry => entry.Id);
		StartForWorkerOptions = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory);

		try {
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = new JobNodeId(LeafNodeId) },
				cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
			return;
		}

		var leafId = new JobNodeId(LeafNodeId);
		ActiveSessions = await jobTrackClient.Query.GetActiveSessionsAsync(new() { Context = context, LeafWorkIds = [leafId] }, cancellationToken);
		var capabilities = await jobTrackClient.Query.GetSessionManageCapabilitiesAsync(
			new() { Context = context, LeafWorkIds = [leafId] }, cancellationToken);
		CanManage = capabilities.FirstOrDefault(c => c.LeafWorkId == leafId)?.CanManage ?? false;
		if (CurrentNode.Node.HasLeafWork) {
			var leafWork = await jobTrackClient.Query.GetLeafWorkAsync(
				new() { Context = context, JobNodeId = leafId }, cancellationToken);
			CurrentAchievement = leafWork.Achievement;
		}

		var workedByUserId = ResolveWorkerFilter(leafId, actor);

		try {
			var sessions = await jobTrackClient.Query.GetLeafSessionsAsync(
				new() { Context = context, LeafWorkId = leafId, WorkedByUserId = workedByUserId }, cancellationToken);

			Panel = new() {
				LeafNodeId = LeafNodeId,
				ViewerZone = ViewerZone,
				DisplayedWorkedByUserId = workedByUserId?.Value,
				DisplayedWorkedByName = workedByUserId.HasValue
					? EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, workedByUserId.Value.Value, "Unknown")
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
	///     The effective worker filter for this leaf's Sessions panel, honoring remembered choices
	///     (browse-sessions filter memory): an explicit <c>WorkedByUserId</c> query value is used and
	///     remembered (empty = "Everyone"); with no query value the last remembered choice for this
	///     leaf is recalled; failing that the default is "Everyone" when the viewer may manage this
	///     leaf's sessions, else their own sessions (a plain worker may not read everyone's). This is
	///     why returning here after correcting a session no longer snaps back to the corrected worker —
	///     the redirect carries no filter, so the remembered choice (or the default) applies instead.
	/// </summary>
	private AppUserId? ResolveWorkerFilter(JobNodeId leafId, AppUserId actor)
	{
		var key = FormattableString.Invariant($"Jobs.Work.WorkedBy.{leafId.Value}");
		// Default when nothing is remembered: Everyone (null) when the viewer may manage this leaf's
		// sessions, else their own (a plain worker may not read everyone's).
		var fallback = CanManage ? (long?)null : actor.Value;
		var resolved = FilterMemory.Resolve(
			HttpContext.Session, key, Request.Query.ContainsKey(nameof(WorkedByUserId)), WorkedByUserId, fallback);
		return resolved.HasValue ? new AppUserId(resolved.Value) : null;
	}

	/// <summary>
	///     Worker-filter options for the sessions panel: an "Everyone" default (empty value,
	///     clearing the filter back to every worker's sessions) followed by each employee, with the
	///     currently filtered worker preselected.
	/// </summary>
	private List<SelectListItem> BuildWorkerFilterOptions(AppUserId? selectedId)
	{
		var options = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory, new SelectListItem("Everyone", string.Empty));
		if (selectedId is AppUserId id) {
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
