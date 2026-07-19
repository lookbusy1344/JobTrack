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
	private const string RootTitle = "Root";

	private const string TruncationSuffix = "…";

	internal static string Title(JobNodeResult node) => Title(node.Description, node.Id.Value, node.Kind);

	internal static string Title(JobNodeResult node, int maxDescriptionLength) =>
		Title(Truncate(node.Description, maxDescriptionLength), node.Id.Value, node.Kind);

	internal static string Title(JobNodeSummaryResult node) => Title(node.Description, node.Id.Value, node.Kind);

	internal static string Title(JobNodeAncestorResult node) => Title(node.Description, node.Id.Value, node.Kind);

	internal static string Title(JobNodeAncestorResult node, int maxDescriptionLength) =>
		Title(Truncate(node.Description, maxDescriptionLength), node.Id.Value, node.Kind);

	internal static string Title(AwaitingProgressEntry entry) => Title(entry.Description, entry.Id.Value);

	internal static string Title(string description, long id) =>
		$"{description} (ID {id.ToString(CultureInfo.InvariantCulture)})";

	private static string Title(string description, long id, NodeKind kind) =>
		kind == NodeKind.Root ? RootTitle : Title(description, id);

	private static string Truncate(string value, int maxLength) =>
		value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), TruncationSuffix);
}
