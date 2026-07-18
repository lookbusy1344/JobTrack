namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IRequestCommands.GetDetailAsync" /> (ADR 0034, plan §7/§8 <c>/Requests/{id}</c>).</summary>
public sealed record GetJobRequestDetailRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The request's anchor node.</summary>
	public required JobNodeId NodeId { get; init; }
}
