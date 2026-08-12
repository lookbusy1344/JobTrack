namespace JobTrack.Web.Pages.Jobs;

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
///     Which other jobs this leaf's own workers were clocked on to at the same time as its sessions
///     (<see cref="IJobQueries.GetConcurrentWorkAsync" />), grouped by worker. Linked from Browse beside
///     the Cost field and named as a link back to it (ADR 0044's navigation rule).
///     <para>
///         Leaf-only for now: work sessions attach to <c>leaf_work</c>, so a branch has no sessions of
///         its own and would report nothing — the branch case is a future subtree aggregation, not a
///         missing filter. The page therefore refuses a branch outright rather than showing a
///         convincing empty table.
///     </para>
///     <para>
///         Gated by <see cref="JobTrackPolicyNames.AnyEmployee" />, matching Browse and the sessions
///         panel: recorded work is job data every employee role may read (ADR 0041). No cost is shown
///         anywhere here, so it needs no rate gate — the figures are raw recorded overlap, and cost
///         provenance stays on <c>/Jobs/CostReport</c>.
///     </para>
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyEmployee)]
public sealed class ConcurrentWorkModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	IClock clock) : PageModel
{
	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	/// <summary>Captured once per request, per ADR 0016's "one captured instant per operation".</summary>
	public Instant Now { get; } = clock.GetCurrentInstant();

	[BindProperty(SupportsGet = true)]
	[Display(Name = "Node")]
	public long NodeId { get; init; }

	public string? ErrorMessage { get; private set; }

	public JobNodeDetailResult? Node { get; private set; }

	public ConcurrentWorkResult? ConcurrentWork { get; private set; }

	/// <summary>The signed-in actor's own time zone, for formatting every timestamp on this page (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	/// <summary>Every row on the page, grouped into one block per worker in the query's own worker order.</summary>
	public IReadOnlyList<IGrouping<AppUserId, ConcurrentWorkRow>> RowsByWorker =>
		ConcurrentWork is null ? [] : [.. ConcurrentWork.Rows.GroupBy(row => row.WorkedByUserId)];

	/// <summary>
	///     Whether this row's overlap was still running when the report was taken. The port clips an
	///     unfinished session to the query's own <see cref="ConcurrentWorkResult.AsOf" />, so an overlap
	///     ending exactly there is one where at least one of the two sessions had not finished — the end
	///     is the moment the page was built, not a moment anything stopped. Compared against the result's
	///     own <c>AsOf</c> rather than <see cref="Now" />: they are the same instant today, but only the
	///     former is what the rows were actually clipped to.
	/// </summary>
	/// <exception cref="ArgumentNullException"><paramref name="row" /> is <see langword="null" />.</exception>
	public bool IsStillRunning(ConcurrentWorkRow row)
	{
		ArgumentNullException.ThrowIfNull(row);

		return ConcurrentWork is not null && row.LastOverlapEnd == ConcurrentWork.AsOf;
	}

	/// <summary>
	///     Formats a worker id for display, with the same directory fallback as every other page that
	///     names an employee (<see cref="EmployeeDirectoryDisplay" />).
	/// </summary>
	public string DescribeWorker(AppUserId workerId) => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, workerId.Value);

	/// <summary>
	///     Formats a concurrent job's owner, which — unlike the worker who recorded the session — may be
	///     nobody at all (the unassigned pickup pool, ownership model §2.1).
	/// </summary>
	public string DescribeOwner(AppUserId? ownerUserId) => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, ownerUserId?.Value);

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await userManager.GetUserAsync(User);
		if (actor is null) {
			return Challenge();
		}

		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor.AppUserId, cancellationToken);
		var context = new CommandContext {
			Actor = actor.AppUserId,
			CorrelationId = Guid.NewGuid(),
		};
		var nodeId = new JobNodeId(NodeId);

		try {
			Node = await jobTrackClient.Query.GetJobNodeAsync(new() {
				Context = context,
				NodeId = nodeId,
			}, cancellationToken);
			if (Node.Node.Kind != NodeKind.Leaf) {
				ErrorMessage = "Concurrent work is reported for a leaf job only.";
				return Page();
			}

			var directory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(new() {
				Context = context,
			}, cancellationToken);
			_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);

			ConcurrentWork = await jobTrackClient.Query.GetConcurrentWorkAsync(
				new() {
					Context = context,
					NodeId = nodeId,
					AsOf = Now,
				}, cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}

		return Page();
	}
}
