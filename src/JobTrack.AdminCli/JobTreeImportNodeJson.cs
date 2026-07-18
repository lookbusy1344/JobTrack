namespace JobTrack.AdminCli;

/// <summary>
///     Wire shape for one row of an <c>import-tree</c> JSON input file, deserialized directly
///     by <c>System.Text.Json</c> and then mapped onto <see cref="JobTreeImportNode" /> (which cannot be
///     a deserialization target itself: its <c>PrerequisiteIds</c> is an <c>EquatableArray&lt;long&gt;</c>
///     for record value semantics, and <c>System.Text.Json</c> cannot populate that collection type
///     directly).
/// </summary>
public sealed class JobTreeImportNodeJson
{
	public required long Id { get; init; }

	public long? ParentId { get; init; }

	public required string Title { get; init; }

	public List<long>? PrerequisiteIds { get; init; }

	/// <summary>How long before the import this leaf's work started, e.g. <c>"2 days"</c>.</summary>
	public string? Open { get; init; }

	/// <summary>
	///     How long before the import this leaf's work finished, e.g. <c>"1 day"</c>. Requires
	///     <see cref="Open" />.
	/// </summary>
	public string? Closed { get; init; }

	/// <summary>
	///     The absolute ISO 8601 instant this leaf's work started, e.g. <c>"2026-07-10T09:00:00Z"</c> —
	///     the alternative spelling to <see cref="Open" />, never combined with it.
	/// </summary>
	public string? Start { get; init; }

	/// <summary>The absolute ISO 8601 instant this leaf's work finished. Requires <see cref="Start" />.</summary>
	public string? End { get; init; }

	/// <summary>
	///     How a closed leaf ended — <c>success</c> (the default), <c>cancelled</c>, or
	///     <c>unsuccessful</c>. Only meaningful alongside <see cref="Closed" />/<see cref="End" />.
	/// </summary>
	public string? Outcome { get; init; }
}
