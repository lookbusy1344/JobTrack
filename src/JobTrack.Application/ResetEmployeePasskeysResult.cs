namespace JobTrack.Application;

/// <summary>The outcome of an administrator passkey reset — a count only, never credential detail.</summary>
public sealed record ResetEmployeePasskeysResult
{
	/// <summary>How many passkeys were removed from the target account.</summary>
	public required int RemovedCount { get; init; }
}
