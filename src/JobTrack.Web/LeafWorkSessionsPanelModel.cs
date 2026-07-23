namespace JobTrack.Web;

using System.Globalization;
using Abstractions;
using Application;
using Microsoft.AspNetCore.Mvc.Rendering;
using NodaTime;

/// <summary>
///     View model for the <c>_LeafWorkSessions</c> partial shared by <c>Browse</c>'s leaf detail view
///     and <c>Work</c>: the session history table, the worked-by picker, and each row's "Finish" action.
///     <see cref="ExtraHiddenFields" /> carries whatever page-specific state each host page's own bound
///     properties need repopulated from the posted form on redisplay (Browse's <c>NodeId</c>/filter
///     query state vs. Work's <c>LeafNodeId</c>) — ASP.NET Core's <c>[BindProperty(SupportsGet = true)]</c>
///     rebinds from posted form field names matching the host page's own property names, which differ
///     between the two hosts.
/// </summary>
public sealed class LeafWorkSessionsPanelModel
{
	public required long LeafNodeId { get; init; }

	/// <summary>The viewing employee's own time zone, for formatting every session's <c>StartedAt</c>/<c>FinishedAt</c> (<see cref="InstantDisplay" />).</summary>
	public required DateTimeZone ViewerZone { get; init; }

	/// <summary>
	///     The single worker the list is narrowed to, or <see langword="null" /> (the default)
	///     when showing every worker's sessions on the leaf — recorded work is job data every employee
	///     may read (ADR 0041), so the whole record is the entry point and the worker picker is a
	///     follow-up filter.
	/// </summary>
	public required long? DisplayedWorkedByUserId { get; init; }

	/// <summary>
	///     Display name of <see cref="DisplayedWorkedByUserId" />, or <see langword="null" /> when
	///     unfiltered.
	/// </summary>
	public required string? DisplayedWorkedByName { get; init; }

	/// <summary>
	///     Whether the worker-filter form renders at all. Browse's leaf detail view always shows every
	///     worker's sessions (recorded work is job data every employee may read, ADR 0041) and has no
	///     use for narrowing it there; <see cref="WorkModel" /> keeps the filter as a follow-up narrowing
	///     tool on its own dedicated page.
	/// </summary>
	public required bool ShowWorkerFilter { get; init; }

	public required EquatableArray<WorkSessionResult> Sessions { get; init; }

	public required List<SelectListItem> WorkedByOptions { get; init; }

	public required IReadOnlyDictionary<string, string?> ExtraHiddenFields { get; init; }

	/// <summary>
	///     Resolves each row's own <c>worked_by</c> to a display name — the list can span
	///     several workers, so the row, not the panel header, carries whose session it is.
	/// </summary>
	public required IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> EmployeeDirectoryById { get; init; }

	/// <summary>
	///     <see cref="ExtraHiddenFields" /> plus the identity and version of <paramref name="session" />,
	///     for the row's forms acting on that session (finish, and finishing at a chosen time).
	/// </summary>
	public IReadOnlyDictionary<string, string?> SessionFields(WorkSessionResult session)
	{
		ArgumentNullException.ThrowIfNull(session);

		return new Dictionary<string, string?>(ExtraHiddenFields) {
			["sessionId"] = session.Id.Value.ToString(CultureInfo.InvariantCulture),
			["version"] = session.Version.ToString(CultureInfo.InvariantCulture),
		};
	}

	// Row actions (finish/correct) are rendered unconditionally and enforced by the command, which
	// re-evaluates WorkSessionAccessPolicy.CanManage's node-control rule per call. That is deliberate:
	// spec §7.3 ("hiding a control in Razor is not authorization") and the fact that node control is
	// not derivable in the view without duplicating domain authorization here, where it could diverge.
	public string DescribeWorker(AppUserId workedByUserId) =>
		EmployeeDirectoryDisplay.Describe(EmployeeDirectoryById, workedByUserId.Value, "Unknown");
}
