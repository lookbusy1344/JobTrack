namespace JobTrack.Application;

using System.Globalization;
using System.Text;
using NodaTime;

/// <summary>
///     Encodes/decodes the opaque <see cref="AuditEventSearchRequest.Cursor" />/
///     <see cref="AuditEventSearchResult.ContinuationCursor" /> token around an
///     <see cref="AuditEventSearchCursor" /> (fresh-eyes review §2.3). The token is Base64Url over
///     <c>"{OccurredAt ticks}:{Id}"</c> -- opaque to callers, who round-trip it verbatim rather than
///     parsing it.
/// </summary>
internal static class AuditEventCursorCodec
{
	private const char Separator = ':';

	public static string Encode(AuditEventSearchCursor cursor)
	{
		var payload = string.Create(
			CultureInfo.InvariantCulture,
			$"{cursor.OccurredAt.ToUnixTimeTicks()}{Separator}{cursor.Id.Value}");

		return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	public static bool TryDecode(string token, out AuditEventSearchCursor cursor)
	{
		cursor = null!;

		string payload;
		try {
			var base64 = token.Replace('-', '+').Replace('_', '/');
			base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
			payload = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
		}
		catch (FormatException) {
			return false;
		}

		var parts = payload.Split(Separator);
		if (parts.Length != 2
			|| !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
			|| !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
			return false;
		}

		cursor = new() { OccurredAt = Instant.FromUnixTimeTicks(ticks), Id = new(id) };
		return true;
	}
}
