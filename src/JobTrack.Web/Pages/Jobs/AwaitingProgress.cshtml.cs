namespace JobTrack.Web.Pages.Jobs;

using Abstractions;
using Application;
using Domain.Hierarchy;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

/// <summary>
///     The flat "jobs awaiting progress" dashboard: leaves only, in priority/deadline order, for one
///     employee or everyone, optionally scoped to a subtree (linked from <c>Browse</c>'s toolbar), with
///     a one-click "Start work" per row (<see cref="IWorkCommands.StartWorkAsync" />) since this page is
///     precisely where a leaf needing exactly that action is surfaced. Viewing job data carries no
///     ownership-based authorization gate (spec §7.3), so the page uses the broad "any employee" policy,
///     matching <c>Browse</c>; the start-work handler carries no additional page-level policy either —
///     the command itself re-evaluates authorization per call.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyEmployee)]
public sealed class AwaitingProgressModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty(SupportsGet = true)] public long? OwnerUserId { get; init; }

	/// <summary>
	///     When set, overrides <see cref="OwnerUserId" /> to show only the unassigned pool
	///     (ownership model §2.1) -- surfaces ready but unclaimed work.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool UnassignedOnly { get; init; }

	[BindProperty(SupportsGet = true)] public long? SubtreeRootId { get; init; }

	public string? ErrorMessage { get; private set; }

	public string? SuccessMessage { get; private set; }

	public JobNodeDetailResult? SubtreeRoot { get; private set; }

	public EquatableArray<AwaitingProgressEntry> Entries { get; private set; } = [];

	public IReadOnlyDictionary<JobNodeId, WorkSessionResult> ActiveSessionByLeaf { get; private set; } =
		new Dictionary<JobNodeId, WorkSessionResult>();

	public List<SelectListItem> OwnerOptions { get; private set; } = [];

	/// <summary>
	///     Formats an owner id for display: display name and username when it resolves in
	///     the loaded workflow-employee directory, otherwise a fallback that still names the numeric
	///     id (covers an owner disabled or role-revoked since assignment — see
	///     <see cref="IJobQueries.GetEmployeeDirectoryAsync" />).
	/// </summary>
	public string DescribeOwner(AppUserId? ownerUserId) =>
		EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, ownerUserId?.Value);

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostStartWorkAsync(long jobNodeId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Work.StartWorkAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					JobNodeId = new(jobNodeId),
					WorkedByUserId = actor.Value,
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
				? "You already have an active session for this leaf."
				: ex.Message;
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied.";
		}

		await LoadAsync(actor.Value, cancellationToken);
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
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		var directory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		OwnerOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Everyone", string.Empty));

		try {
			if (SubtreeRootId is { } subtreeRootId) {
				SubtreeRoot = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = new JobNodeId(subtreeRootId) },
					cancellationToken);
			}

			var ownership = (UnassignedOnly, OwnerUserId) switch {
				(true, _) => OwnershipFilter.Unassigned,
				(false, { } ownerUserId) => OwnershipFilter.OwnedBy(new(ownerUserId)),
				(false, null) => OwnershipFilter.All,
			};

			Entries = await jobTrackClient.Query.GetAwaitingProgressAsync(
				new() { Context = context, Ownership = ownership, SubtreeRootId = SubtreeRootId is { } id ? new JobNodeId(id) : null },
				cancellationToken);

			await LoadActiveSessionsAsync(context, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
	}

	private async Task LoadActiveSessionsAsync(CommandContext context, CancellationToken cancellationToken)
	{
		var leafIds = Entries.Select(entry => entry.Id).ToArray();
		if (leafIds.Length == 0) {
			ActiveSessionByLeaf = new Dictionary<JobNodeId, WorkSessionResult>();
			return;
		}

		try {
			var sessions = await jobTrackClient.Query.GetActiveSessionsAsync(new() { Context = context, LeafWorkIds = [.. leafIds] },
				cancellationToken);

			ActiveSessionByLeaf = sessions.ToDictionary(s => s.LeafWorkId);
		}
		catch (AuthorizationDeniedException) {
			ActiveSessionByLeaf = new Dictionary<JobNodeId, WorkSessionResult>();
		}
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}
}
