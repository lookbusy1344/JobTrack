namespace JobTrack.Web;

using System.Globalization;
using Abstractions;
using Application;
using Domain.Hierarchy;

/// <summary>
///     Shared formatting for a job node's display title across every page that shows one — always
///     paired with its numeric id ("Network tasks (ID 12)") so an actor working from a report, a URL,
///     or a support ticket that only carries the id can match it back to what is on screen. Overloaded
///     per result type rather than a single "Node" parameter: <see cref="JobNodeResult" />,
///     <see cref="JobNodeSummaryResult" />, <see cref="JobNodeAncestorResult" />, and
///     <see cref="AwaitingProgressEntry" /> are unrelated sealed records spanning two library projects
///     (<c>JobTrack.Application</c>, <c>JobTrack.Domain</c>) with no shared interface between them —
///     adding one there would extend those projects' public-API surface (house style: reviewed against
///     the Framework Design Guidelines, gate §7.5) purely to support this Web-only display convenience,
///     so the overloads live here instead, each carrying its own <c>Id</c>/<c>Description</c> pair.
///     The permanent root is the one node whose id an actor never needs to look up externally, so it
///     renders uniquely as plain "Root" rather than "Root (ID 1)" wherever its <see cref="NodeKind" />
///     is known.
/// </summary>
internal static class JobNodeDisplay
{
	/// <summary>
	///     Keeps a node title readable in a table row or list item — the node's own page (one tap away
	///     via the same link) is where the full text lives. Declared here rather than per page so every
	///     list in the staff and requester UIs clips at the same width; a page heading naming its own
	///     subject is the deliberate exception and renders untruncated.
	/// </summary>
	internal const int RowTitleMaxDescriptionLength = 100;

	/// <summary>
	///     The tighter budget for a breadcrumb trail, where several titles share one line.
	/// </summary>
	internal const int BreadcrumbMaxDescriptionLength = 50;

	private const string RootTitle = "Root";

	private const string TruncationSuffix = "…";

	internal static string Title(JobNodeResult node) => Title(node.Description, node.Id.Value, node.Kind);

	internal static string Title(JobNodeResult node, int maxDescriptionLength) =>
		Title(node.Description, node.Id.Value, maxDescriptionLength, node.Kind);

	internal static string Title(JobNodeSummaryResult node) => Title(node.Description, node.Id.Value, node.Kind);

	internal static string Title(JobNodeSummaryResult node, int maxDescriptionLength) =>
		Title(node.Description, node.Id.Value, maxDescriptionLength, node.Kind);

	internal static string Title(JobNodeAncestorResult node) => Title(node.Description, node.Id.Value, node.Kind);

	internal static string Title(JobNodeAncestorResult node, int maxDescriptionLength) =>
		Title(node.Description, node.Id.Value, maxDescriptionLength, node.Kind);

	internal static string Title(AwaitingProgressEntry entry) => Title(entry.Description, entry.Id.Value);

	internal static string Title(AwaitingProgressEntry entry, int maxDescriptionLength) =>
		Title(entry.Description, entry.Id.Value, maxDescriptionLength);

	internal static string Title(string description, long id) =>
		string.Create(CultureInfo.InvariantCulture, $"{description} (ID {id})");

	internal static string Title(string description, long id, int maxDescriptionLength) =>
		description.Length <= maxDescriptionLength
			? Title(description, id)
			: string.Create(
				CultureInfo.InvariantCulture,
				$"{Clip(description, maxDescriptionLength)}{TruncationSuffix} (ID {id})");

	internal static string Truncate(string value, int maxLength) =>
		value.Length <= maxLength ? value : string.Concat(Clip(value, maxLength), TruncationSuffix);

	/// <summary>
	///     Slices <paramref name="value" /> to at most <paramref name="maxLength" /> characters and drops
	///     any whitespace the cut exposes, so a break landing on a word gap does not leave a dead space
	///     stranded before the ellipsis. Returns a view over the caller's string — no allocation — leaving
	///     the single allocation to whichever caller builds the finished title.
	/// </summary>
	private static ReadOnlySpan<char> Clip(string value, int maxLength) => value.AsSpan(0, maxLength).TrimEnd();

	private static string Title(string description, long id, NodeKind kind) =>
		kind == NodeKind.Root ? RootTitle : Title(description, id);

	private static string Title(string description, long id, int maxDescriptionLength, NodeKind kind) =>
		kind == NodeKind.Root ? RootTitle : Title(description, id, maxDescriptionLength);
}
