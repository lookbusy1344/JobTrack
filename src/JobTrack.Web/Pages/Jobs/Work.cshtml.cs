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
///     The unified leaf-work page (unified-leaf-workflow plan, ADR 0045): the single interactive
///     surface for a leaf's current status and its Sessions. Shows one obvious primary action for the
///     current state -- Start, the Pause/Complete decision, or Reopen and start -- and pushes
///     historical correction and exceptional outcomes into progressive disclosure. Carries no
///     page-level authorization policy beyond <see cref="JobTrackPolicyNames.JobWorkflow" /> -- every
///     command re-evaluates its own authorization at write time, so <see cref="LeafWorkPageResult" />'s
///     <c>Can*</c> members are rendering hints only.
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

	/// <summary>
	///     Set by an End-session link from Browse/Awaiting Progress (<c>?endSessionId=</c>) so the page
	///     can focus the end-session decision panel on load; the command itself never trusts this value
	///     -- it always reloads and reauthorizes the exact confirmed session set.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public long? EndSessionId { get; init; }

	/// <summary>The unified bounded projection this page renders (<see cref="IJobQueries.GetLeafWorkPageAsync" />).</summary>
	public LeafWorkPageResult? WorkPage { get; private set; }

	public LeafWorkSessionsPanelModel? Panel { get; private set; }

	/// <summary>The signed-in actor, so the page can tell their own active session apart from another worker's.</summary>
	public AppUserId? CurrentActorId { get; private set; }

	/// <summary>Every enabled workflow employee, for the "Start for…" and reopen-target worker pickers.</summary>
	public IReadOnlyList<SelectListItem> StartForWorkerOptions { get; private set; } = [];

	/// <summary>Every enabled workflow employee, with the viewer selected by default for reopen-and-start.</summary>
	public IReadOnlyList<SelectListItem> ReopenWorkerOptions { get; private set; } = [];

	/// <summary>The one leaf this page is for, as a <see cref="WorkRowActionsModel" /> so the toolbar shares Browse's exact start/finish/start-for logic.</summary>
	public WorkRowActionsModel ToolbarActions => new() {
		LeafNodeId = LeafNodeId,
		ViewerId = CurrentActorId ?? new AppUserId(0),
		ActiveSessions = WorkPage?.ActiveSessions ?? [],
		CanManage = WorkPage?.CanManageSessions ?? false,
		Achievement = WorkPage?.Achievement,
		IsArchived = WorkPage?.ArchivedAt is not null,
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

	/// <summary>"Pause work": finishes exactly one session and leaves achievement unchanged (ADR 0045 §3.3).</summary>
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
			SuccessMessage = "Ends this session; the job stays In Progress.";
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

	/// <summary>
	///     "Complete job": atomically finishes the exact confirmed active-session set shown on the page
	///     and records <see cref="Achievement.Success" /> (ADR 0045 §1/§3). <paramref name="sessionId" />/
	///     <paramref name="sessionVersion" /> are parallel arrays, one hidden pair per session the page
	///     rendered in its review -- the command re-verifies this is exactly the leaf's current active
	///     set before finishing any of them.
	/// </summary>
	public async Task<IActionResult> OnPostCompleteAsync(
		long leafWorkVersion, long[]? sessionId, long[]? sessionVersion, string? finishedAt, string? completionNote,
		CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		sessionId ??= [];
		sessionVersion ??= [];
		if (sessionId.Length != sessionVersion.Length) {
			ErrorMessage = "The active-session list on this page is out of date. Reloading.";
			return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
		}

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(finishedAt, zone, out var finishedAtInstant)) {
				ErrorMessage = "Enter a valid completion date and time.";
				return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
			}

			var result = await jobTrackClient.Work.CompleteLeafAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				JobNodeId = new(LeafNodeId),
				Version = leafWorkVersion,
				ExpectedActiveSessions = [
					.. sessionId.Zip(sessionVersion, (id, ver) => new ExpectedActiveSession { Id = new(id), Version = ver }),
				],
				FinishedAt = finishedAtInstant,
				CompletionNote = string.IsNullOrWhiteSpace(completionNote) ? null : completionNote,
			}, cancellationToken);
			SuccessMessage = result.FinishedSessions.Count switch {
				0 => "Job completed.",
				1 => "Job completed and session finished.",
				var n => $"Job completed and {n} sessions finished.",
			};
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "This leaf has no work attached.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage =
				"Someone else changed this leaf or one of its active sessions since the page was loaded. The latest state is shown below.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied, so it cannot be marked complete.";
		}

		return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
	}

	/// <summary>
	///     "Reopen and start session": atomically reopens a terminal leaf and starts
	///     <paramref name="workedByUserId" />'s session (ADR 0045 §1/§2). Authorized more widely than
	///     the advanced "Reopen without starting" action below -- see <see cref="LeafWorkPageResult.CanReopenAndStartForSelf" />/
	///     <see cref="LeafWorkPageResult.CanReopenAndStartForOthers" />, both rendering hints only.
	/// </summary>
	public async Task<IActionResult> OnPostReopenAndStartAsync(
		long leafWorkVersion, string? reason, long workedByUserId, string? startedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		if (string.IsNullOrWhiteSpace(reason)) {
			ErrorMessage = "Enter a reason for reopening this job.";
			return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
		}

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(startedAt, zone, out var startedAtInstant)) {
				ErrorMessage = "Enter a valid date and time.";
				return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
			}

			_ = await jobTrackClient.Work.ReopenAndStartWorkAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				JobNodeId = new(LeafNodeId),
				Version = leafWorkVersion,
				Reason = reason,
				WorkedByUserId = new(workedByUserId),
				StartedAt = startedAtInstant,
			}, cancellationToken);
			SuccessMessage = "Job reopened. Session started.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "This leaf or worker does not exist.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this leaf since the page was loaded. The latest state is shown below.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}

		return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
	}

	/// <summary>
	///     Every advanced achievement action that is not one of the two atomic composites above --
	///     Cancel job, Mark unsuccessful, and Reopen without starting (ADR 0045 §2/§5.5) -- all reuse the
	///     one primitive <see cref="IWorkCommands.SetAchievementAsync" />, which re-evaluates
	///     <see cref="Domain.Authorization.AchievementAccessPolicy" /> itself, including reopening's own
	///     Administrator/JobManager-only rule.
	/// </summary>
	public async Task<IActionResult> OnPostSetAchievementAsync(
		long leafWorkVersion, Achievement newAchievement, string? reason, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		if (string.IsNullOrWhiteSpace(reason)) {
			ErrorMessage = "Enter a reason for this change.";
			return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
		}

		try {
			_ = await jobTrackClient.Work.SetAchievementAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				JobNodeId = new(LeafNodeId),
				NewAchievement = newAchievement,
				Reason = reason,
				Version = leafWorkVersion,
			}, cancellationToken);
			SuccessMessage = newAchievement == Achievement.Waiting
				? "Job reopened."
				: $"Achievement changed to {EnumDisplay.Label(newAchievement)}.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "This leaf has no work attached.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage =
				"Someone else changed this leaf's achievement since the page was loaded. The latest state is shown below.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied, so it cannot be marked complete.";
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
		ReopenWorkerOptions = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory);
		foreach (var option in ReopenWorkerOptions) {
			option.Selected = option.Value == actor.Value.ToString(CultureInfo.InvariantCulture);
		}

		var leafId = new JobNodeId(LeafNodeId);
		try {
			WorkPage = await jobTrackClient.Query.GetLeafWorkPageAsync(new() { Context = context, JobNodeId = leafId }, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
			return;
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
		var fallback = WorkPage?.CanManageSessions ?? false ? (long?)null : actor.Value;
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
