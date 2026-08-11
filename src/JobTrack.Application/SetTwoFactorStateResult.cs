namespace JobTrack.Application;

/// <summary>Updated account state after a two-factor transition.</summary>
public sealed class SetTwoFactorStateResult
{
	/// <summary>The new security stamp after the credential transition.</summary>
	public required string SecurityStamp { get; init; }

	/// <summary>The new concurrency stamp after the credential transition.</summary>
	public required string ConcurrencyStamp { get; init; }

	/// <summary>The persisted two-factor enabled flag.</summary>
	public bool TwoFactorEnabled { get; init; }

	/// <summary>The persisted two-factor-enabled instant as a public-boundary value.</summary>
	public DateTimeOffset? TwoFactorEnabledAt { get; init; }
}
