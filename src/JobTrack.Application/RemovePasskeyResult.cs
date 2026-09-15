namespace JobTrack.Application;

/// <summary>Updated account state after removing a passkey.</summary>
public sealed class RemovePasskeyResult
{
	/// <summary>The new security stamp after the credential transition.</summary>
	public required string SecurityStamp { get; init; }

	/// <summary>The new concurrency stamp after the credential transition.</summary>
	public required string ConcurrencyStamp { get; init; }
}
