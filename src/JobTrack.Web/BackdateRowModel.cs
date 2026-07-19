namespace JobTrack.Web;

/// <summary>The <c>_BackdateRow</c> partial's model: a backdate form plus the table's own column count.</summary>
public sealed class BackdateRowModel
{
	public required BackdateDisclosureModel Disclosure { get; init; }

	/// <summary>
	///     The host table's total column count (matching its own <c>&lt;thead&gt;</c>), so the row's
	///     single cell can span the full table width. An oversized value is harmless — browsers clamp a
	///     <c>colspan</c> to the table's actual column count — so this only needs to match the markup
	///     it's authored beside.
	/// </summary>
	public required int ColumnCount { get; init; }
}
