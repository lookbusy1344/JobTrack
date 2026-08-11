namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IJobCommands.ImportSubtreeAsync" />.</summary>
public sealed record ImportSubtreeResult
{
	/// <summary>Every created node, mapping request-local id back to real <c>job_node</c> identifier.</summary>
	public required EquatableArray<ImportedJobNode> Nodes { get; init; }
}
