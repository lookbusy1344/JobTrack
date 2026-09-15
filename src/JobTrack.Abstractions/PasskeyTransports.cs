namespace JobTrack.Abstractions;

using System.Text.Json;

/// <summary>
///     The one canonical serialization of a WebAuthn credential's transport strings for the
///     <c>identity_user_passkey.transports</c> column (ADR 0071 §6.1). A JSON array of strings, so the
///     column round-trips through both the atomic enrol path and <c>IUserPasskeyStore</c> without the
///     two agreeing on any other encoding. Empty or absent transports persist as <c>null</c>.
/// </summary>
public static class PasskeyTransports
{
	/// <summary>Serializes the framework's transport strings to the canonical column value, or null when there are none.</summary>
	public static string? Serialize(IReadOnlyList<string>? transports) =>
		transports is null || transports.Count == 0 ? null : JsonSerializer.Serialize(transports);

	/// <summary>Parses a stored column value back to the framework's transport strings; a null or blank value yields an empty array.</summary>
	public static string[] Parse(string? stored) =>
		string.IsNullOrWhiteSpace(stored) ? [] : JsonSerializer.Deserialize<string[]>(stored) ?? [];
}
