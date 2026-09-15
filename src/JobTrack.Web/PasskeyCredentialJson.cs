namespace JobTrack.Web;

using System.Text;
using Abstractions;

/// <summary>Reads a passkey ceremony body without allowing oversized JSON to reach the framework parser.</summary>
internal static class PasskeyCredentialJson
{
	public static async Task<string?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.ContentLength is long contentLength && contentLength > PasskeyPolicy.MaximumCredentialJsonByteLength) {
			return null;
		}

		using var reader = new StreamReader(request.Body, Encoding.UTF8, false, leaveOpen: true);
		var buffer = new char[PasskeyPolicy.MaximumCredentialJsonByteLength + 1];
		var count = await reader.ReadBlockAsync(buffer, cancellationToken);
		if (count == 0 || count > PasskeyPolicy.MaximumCredentialJsonByteLength) {
			return null;
		}

		var json = new string(buffer, 0, count);
		return Encoding.UTF8.GetByteCount(json) <= PasskeyPolicy.MaximumCredentialJsonByteLength ? json : null;
	}
}
