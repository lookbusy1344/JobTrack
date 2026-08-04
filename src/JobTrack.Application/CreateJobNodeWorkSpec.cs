namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Work begun on a node at the moment it is created (<see cref="CreateJobNodeRequest.BeginWork" />).
///     The create transaction attaches <c>LeafWork</c>, advances it
///     <see cref="Achievement.Waiting" /> -&gt; <see cref="Achievement.InProgress" /> under ADR 0038's
///     auto-advance, and opens <see cref="WorkedByUserId" />'s session at the create instant -- the
///     equivalent of <see cref="IWorkCommands.StartWorkAsync" /> against the node that
///     <see cref="IJobCommands.AddChildAsync" /> has just created, without splitting one logical write
///     across two transactions.
///     <para>
///         This is deliberately not <see cref="ImportSubtreeLeafWorkSpec" />: that reconstructs work
///         that already happened, with caller-supplied instants and a chosen end achievement. This one
///         starts work now, so it carries no instants and no achievement -- an <c>InProgress</c> leaf
///         with one open session is its only outcome.
///     </para>
/// </summary>
public sealed record CreateJobNodeWorkSpec
{
	/// <summary>
	///     The employee whose session opens on the new node. When
	///     <see cref="CreateJobNodeRequest.OwnerUserId" /> is <see langword="null" />, this employee also
	///     becomes the new node's owner -- the create-time form of ADR 0048's session-start auto-claim,
	///     which never leaves an actively worked node sitting in the unassigned pool.
	/// </summary>
	public required AppUserId WorkedByUserId { get; init; }
}
