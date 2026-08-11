namespace JobTrack.Web;

using System.Globalization;

/// <summary>
///     The <c>_PickUpButton</c> partial's model: the claim-this-node control shown wherever an
///     unassigned node is listed (Browse's detail record, its search and subtree rows). One model and
///     one partial rather than a form repeated per call site, so the field names the handler binds
///     from are stated once.
/// </summary>
public sealed class PickUpButtonModel
{
	/// <summary>
	///     The field name <c>OnPostPickUpAsync</c> binds its target from. Deliberately not
	///     <c>nodeId</c>: model binding is case-insensitive, so that name would collide with the
	///     hosting page's own <c>NodeId</c> browsing state in <see cref="PageStateFields" /> and claim
	///     the browsed node instead of the row that was clicked.
	/// </summary>
	public const string NodeFieldName = "pickUpNodeId";

	/// <summary>The node this control claims.</summary>
	public required long NodeId { get; init; }

	/// <summary>
	///     The hosting page's filter/route state, replayed as hidden fields so the post lands back on
	///     the same filtered view (mirrors <see cref="WorkRowActionsModel.PageStateFields" />).
	/// </summary>
	public required IReadOnlyDictionary<string, string?> PageStateFields { get; init; }

	/// <summary>Every hidden field the form posts: the page's state plus this control's target.</summary>
	public IReadOnlyDictionary<string, string?> Fields =>
		new Dictionary<string, string?>(PageStateFields) { [NodeFieldName] = NodeId.ToString(CultureInfo.InvariantCulture) };
}
