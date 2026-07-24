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

	/// <summary>The unified bounded projection this page renders (<see cref="IJobQueries.GetLeafWorkPageAsync" />).</summary>
	public LeafWorkPageResult? WorkPage { get; private set; }

	public LeafWorkSessionsPanelModel? Panel { get; private set; }

	/// <summary>
	///     The leaf's full node record — carries <c>WriteUp</c> and the node's own optimistic-concurrency
	///     <c>Version</c> (distinct from <see cref="LeafWorkPageResult.LeafWorkVersion" />), neither of
	///     which the bounded <see cref="WorkPage" /> projection needs for its own purpose.
	/// </summary>
	public JobNodeDetailResult? CurrentNode { get; private set; }

	/// <summary>
	///     The achievements <see cref="IWorkCommands.CompleteLeafAsync" /> can record from
	///     <see cref="Achievement.InProgress" /> (ADR 0047), for the "Completion options" dropdown —
	///     <see cref="Achievement.Success" /> first and selected by default, as the common case.
	/// </summary>
	public IReadOnlyList<SelectListItem> CompletionAchievementOptions { get; } = [
		new(EnumDisplay.Label(Achievement.Success), nameof(Achievement.Success), true),
		new(EnumDisplay.Label(Achievement.Cancelled), nameof(Achievement.Cancelled)),
		new(EnumDisplay.Label(Achievement.Unsuccessful), nameof(Achievement.Unsuccessful)),
	];

	/// <summary>
	///     Every achievement the single "Change outcome" dropdown may target from the leaf's current
	///     achievement: every value <see cref="AchievementTransitions.IsPermitted" /> allows, filtered to
	///     what the actor is authorized for — <see cref="LeafWorkPageResult.CanReopenWithoutStarting" />
	///     for the elevated terminal-to-<see cref="Achievement.Waiting" /> case
	///     (<see cref="AchievementTransitions.IsReopening" />), <see cref="LeafWorkPageResult.CanComplete" />
	///     (the same authority <see cref="IWorkCommands.SetAchievementAsync" /> already requires for every
	///     other transition) otherwise. A rendering hint only — <see cref="OnPostSetAchievementAsync" />
	///     re-authorizes itself regardless of what this list showed.
	/// </summary>
	public IReadOnlyList<SelectListItem> ChangeOutcomeOptions
	{
		get
		{
			if (WorkPage is not { HasLeafWork: true, Achievement: Achievement achievement } workPage) {
				return [];
			}

			List<SelectListItem> options = [];
			foreach (var candidate in Enum.GetValues<Achievement>()) {
				if (candidate == Achievement.None || candidate == achievement
												  || !AchievementTransitions.IsPermitted(achievement, candidate)) {
					continue;
				}

				var authorized = AchievementTransitions.IsReopening(achievement, candidate)
					? workPage.CanReopenWithoutStarting
					: workPage.CanComplete;
				if (authorized) {
					options.Add(new(EnumDisplay.Label(candidate), candidate.ToString()));
				}
			}

			return options;
		}
	}

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

	/// <summary>
	///     "Pause job": finishes exactly one session and leaves achievement unchanged (ADR 0045 §3.3).
	///     <paramref name="nodeVersion" />/<paramref name="writeUp" /> are present only when the post came
	///     from this page's unified ending form, where the write-up shares one form with Pause/Complete/Save
	///     directly. Every other action on this page (Start, Reopen and start, Change outcome, and the
	///     icon-only row action in <c>_LeafWorkSessions</c> reused from <c>/Jobs/Browse</c>) carries no
	///     write-up of its own -- site.js's shared submit listener instead fires a separate SaveWriteUp
	///     request first when a write-up textarea exists elsewhere on the page, so the edit is still saved,
	///     just via a distinct request rather than these two fields on this one.
	/// </summary>
	public async Task<IActionResult> OnPostFinishAsync(
		long sessionId, long version, string? finishedAt, long? nodeVersion = null, string? writeUp = null,
		CancellationToken cancellationToken = default)
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

			var result = await jobTrackClient.Work.FinishSessionAndUpdateWriteUpAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				SessionId = new(sessionId),
				Version = version,
				FinishedAt = finishedAtInstant,
				WriteUpChange = nodeVersion is long expectedNodeVersion
					? new() { NodeVersion = expectedNodeVersion, WriteUp = string.IsNullOrWhiteSpace(writeUp) ? null : writeUp }
					: null,
			}, cancellationToken);
			SuccessMessage = WithWriteUpNote("Ends this session; the job stays In Progress.", result.WriteUpChanged);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That session or job node does not exist.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage =
				"Someone else changed this session or this job's details since the page was loaded. The latest state is shown below.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}

		return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
	}

	/// <summary>
	///     "Complete job": atomically finishes the exact confirmed active-session set shown on the page
	///     and records <paramref name="finalAchievement" /> (<see cref="Achievement.Success" /> by
	///     default, or <see cref="Achievement.Cancelled" />/<see cref="Achievement.Unsuccessful" /> from
	///     the "Completion options" dropdown, ADR 0047). <paramref name="completeSessionId" />/
	///     <paramref name="completeSessionVersion" /> are parallel arrays, one hidden pair per session the
	///     page rendered in its review -- the command re-verifies this is exactly the leaf's current active
	///     set before finishing any of them. They carry a <c>complete</c> prefix (and the backdate is
	///     <paramref name="completionFinishedAt" />) because the unified ending form also carries the
	///     Pause button's single <c>sessionId</c>/<c>version</c> pair, which must stay distinct.
	/// </summary>
	public async Task<IActionResult> OnPostCompleteAsync(
		long leafWorkVersion, long[]? completeSessionId, long[]? completeSessionVersion, string? completionFinishedAt,
		string? completionNote, Achievement finalAchievement = Achievement.Success, long? nodeVersion = null,
		string? writeUp = null, CancellationToken cancellationToken = default)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		completeSessionId ??= [];
		completeSessionVersion ??= [];
		if (completeSessionId.Length != completeSessionVersion.Length) {
			ErrorMessage = "The active-session list on this page is out of date. Reloading.";
			return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
		}

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(completionFinishedAt, zone, out var finishedAtInstant)) {
				ErrorMessage = "Enter a valid completion date and time.";
				return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
			}

			var result = await jobTrackClient.Work.CompleteLeafAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				JobNodeId = new(LeafNodeId),
				Version = leafWorkVersion,
				ExpectedActiveSessions = [
					.. completeSessionId.Zip(
						completeSessionVersion, (id, ver) => new ExpectedActiveSession { Id = new(id), Version = ver }),
				],
				FinishedAt = finishedAtInstant,
				CompletionNote = string.IsNullOrWhiteSpace(completionNote) ? null : completionNote,
				FinalAchievement = finalAchievement,
				WriteUpChange = nodeVersion is long expectedNodeVersion
					? new() { NodeVersion = expectedNodeVersion, WriteUp = string.IsNullOrWhiteSpace(writeUp) ? null : writeUp }
					: null,
			}, cancellationToken);
			SuccessMessage = WithWriteUpNote(
				DescribeCompletionOutcome(result.Achievement, result.FinishedSessions.Count), result.WriteUpChanged);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "This leaf has no work attached.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage =
				"Someone else changed this leaf, one of its active sessions, or this job's details since the page was loaded. The latest state is shown below.";
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
	///     <paramref name="reopenWorkedByUserId" />'s session (ADR 0045 §1/§2). Authorized more widely than
	///     the advanced "Reopen without starting" action below -- see <see cref="LeafWorkPageResult.CanReopenAndStartForSelf" />/
	///     <see cref="LeafWorkPageResult.CanReopenAndStartForOthers" />, both rendering hints only.
	/// </summary>
	public async Task<IActionResult> OnPostReopenAndStartAsync(
		long leafWorkVersion, string? reason, long reopenWorkedByUserId, string? startedAt, CancellationToken cancellationToken)
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
				WorkedByUserId = new(reopenWorkedByUserId),
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
	///     The single "Change outcome" dropdown's Save action: every achievement transition that is not
	///     one of the two atomic composites above (Start-adjacent Complete, Reopen-and-start) reuses this
	///     one primitive, including reopening a terminal leaf back to <see cref="Achievement.Waiting" />
	///     without starting a session. <see cref="Domain.Authorization.AchievementAccessPolicy" />
	///     re-evaluates itself here regardless of what <see cref="ChangeOutcomeOptions" /> rendered,
	///     including reopening's own Administrator/JobManager-only rule.
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

	/// <summary>
	///     Saves the node's <c>WriteUp</c> -- the main way a worker documents how a leaf's (or a
	///     branch's, on <c>/Jobs/Edit</c>) work went, whether it succeeded or failed. <see cref="IJobCommands.EditAsync" />
	///     is full-replace, so this re-fetches the node's other current field values immediately before
	///     saving rather than round-tripping every one of them as a hidden field on this page;
	///     <paramref name="nodeVersion" /> is the node's own optimistic-concurrency version captured when
	///     the page was rendered (<see cref="CurrentNode" />), so a concurrent structural edit (e.g. via
	///     <c>/Jobs/Edit</c>) is still detected as a conflict even though this handler's own fetch is fresh.
	///     This is the write-up's own standalone button -- Pause (<see cref="OnPostFinishAsync" />) and
	///     Complete (<see cref="OnPostCompleteAsync" />) instead carry the same field into
	///     <see cref="IWorkCommands.FinishSessionAndUpdateWriteUpAsync" />/<see cref="IWorkCommands.CompleteLeafAsync" />'s
	///     own optional write-up change (remediation plan §2.1), so a write-up submitted alongside Pause
	///     or Complete commits with that command atomically rather than as a separate mutation.
	/// </summary>
	public async Task<IActionResult> OnPostSaveWriteUpAsync(long nodeVersion, string? writeUp, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var (_, failure) = await SaveWriteUpFirstAsync(actor.Value, nodeVersion, writeUp, cancellationToken);
		if (failure is not null) {
			return failure;
		}

		SuccessMessage = "Write-up saved.";

		return RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId });
	}

	/// <summary>
	///     Backs the write-up's own standalone Save button (<see cref="OnPostSaveWriteUpAsync" /> only --
	///     Pause and Complete no longer call this, see that handler's own doc). A no-op when
	///     <paramref name="nodeVersion" /> is absent (the post came from a form without the field) or when
	///     the submitted text already matches what is stored — an unchanged write-up must not burn a node
	///     version or write an audit entry.
	/// </summary>
	/// <returns>
	///     Whether the write-up was actually changed, and the result to return instead of continuing when
	///     the save itself failed (<see langword="null" /> when the caller should carry on).
	/// </returns>
	private async Task<(bool Saved, IActionResult? Failure)> SaveWriteUpFirstAsync(
		AppUserId actor, long? nodeVersion, string? writeUp, CancellationToken cancellationToken)
	{
		if (nodeVersion is not long version) {
			return (false, null);
		}

		var leafId = new JobNodeId(LeafNodeId);
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };
		var text = string.IsNullOrWhiteSpace(writeUp) ? null : writeUp;

		try {
			var current = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = leafId }, cancellationToken);
			if (text == current.Node.WriteUp) {
				return (false, null);
			}

			_ = await jobTrackClient.Jobs.EditAsync(new() {
				Context = context,
				NodeId = leafId,
				Description = current.Node.Description,
				WriteUp = text,
				OwnerUserId = current.Node.OwnerUserId,
				ExpectedDurationHours = current.Node.ExpectedDurationHours,
				ExpectedCost = current.Node.ExpectedCost,
				NeededStart = current.Node.NeededStart,
				NeededFinish = current.Node.NeededFinish,
				Priority = current.Node.Priority,
				Version = version,
			}, cancellationToken);

			return (true, null);
		}
		catch (AuthorizationDeniedException) {
			return (false, Forbid());
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this job's details since you loaded this page. Reload and try again.";
		}

		return (false, RedirectToPage(new { leafNodeId = LeafNodeId, workedByUserId = WorkedByUserId }));
	}

	/// <summary>Appends the write-up's own confirmation to a command's success message, but only when it actually changed.</summary>
	private static string WithWriteUpNote(string message, bool writeUpSaved) => writeUpSaved ? $"{message} Write-up saved." : message;

	/// <summary>
	///     Describes a <see cref="IWorkCommands.CompleteLeafAsync" /> outcome for <see cref="SuccessMessage" />
	///     (ADR 0047) -- the phrasing for <see cref="Achievement.Success" /> is unchanged from before the
	///     dropdown existed.
	/// </summary>
	private static string DescribeCompletionOutcome(Achievement achievement, int finishedSessionCount)
	{
		var verb = achievement switch {
			Achievement.Success => "completed",
			Achievement.Cancelled => "cancelled",
			Achievement.Unsuccessful => "marked unsuccessful",
			_ => throw new ArgumentOutOfRangeException(nameof(achievement), achievement, "Not a valid completion outcome."),
		};

		return finishedSessionCount switch {
			0 => $"Job {verb}.",
			1 => $"Job {verb} and session finished.",
			var n => $"Job {verb} and {n} sessions finished.",
		};
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
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = leafId }, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
			return;
		}

		var workedByUserId = ResolveWorkerFilter(leafId);

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
				ShowWorkerFilter = true,
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
	///     leaf is recalled; failing that the default is "Everyone" — <see cref="WorkSessionAccessPolicy.CanView" />
	///     (ADR 0041) grants every baseline role unqualified visibility of all workers' sessions, so
	///     there is no permission reason to default to "just me". This is why returning here after
	///     correcting a session no longer snaps back to the corrected worker — the redirect carries no
	///     filter, so the remembered choice (or the default) applies instead.
	/// </summary>
	private AppUserId? ResolveWorkerFilter(JobNodeId leafId)
	{
		var key = FormattableString.Invariant($"Jobs.Work.WorkedBy.{leafId.Value}");
		var resolved = FilterMemory.Resolve(
			HttpContext.Session, key, Request.Query.ContainsKey(nameof(WorkedByUserId)), WorkedByUserId, null);
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
