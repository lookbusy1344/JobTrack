namespace JobTrack.Application;

/// <summary>The renamed passkey's updated display-safe summary. Rename rotates no stamps.</summary>
public sealed class RenamePasskeyResult
{
	/// <summary>The updated summary of the renamed credential.</summary>
	public required PasskeySummary Passkey { get; init; }
}
