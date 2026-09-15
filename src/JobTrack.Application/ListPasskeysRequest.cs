namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for listing the actor's own enrolled passkeys as display-safe summaries.</summary>
public sealed class ListPasskeysRequest
{
	/// <summary>The signed-in account whose passkeys are listed.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The signed-in account's <c>identity_user</c> row.</summary>
	public long IdentityUserId { get; init; }
}
