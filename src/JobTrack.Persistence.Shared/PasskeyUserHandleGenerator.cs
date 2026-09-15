namespace JobTrack.Persistence.Shared;

using System.Buffers.Text;
using System.Security.Cryptography;
using Abstractions;

internal static class PasskeyUserHandleGenerator
{
	public static string Create() =>
		Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(PasskeyPolicy.UserHandleByteLength));
}
