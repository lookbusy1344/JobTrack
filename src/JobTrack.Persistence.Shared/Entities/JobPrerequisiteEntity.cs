namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;

/// <summary>
///     Persistence shape of the <c>job_prerequisite</c> table (schema version 0008): a directed edge
///     <see cref="FromId" /> (the required job) -&gt; <see cref="ToId" /> (the dependent job), keyed on
///     the pair itself. No optimistic-concurrency token -- the edge either exists or it does not.
/// </summary>
internal sealed class JobPrerequisiteEntity
{
	public required JobNodeId FromId { get; set; }

	public required JobNodeId ToId { get; set; }
}
