namespace JobTrack.Application;

using System.Security.Cryptography;
using System.Text;

/// <summary>
///     Generates opaque personal access tokens (ADR 0029). Mirrors the split
///     <see cref="IEmployeeCommands.CreateEmployeeAsync" /> already uses for passwords: the plaintext
///     secret is generated and hashed in this layer, and only the hash ever reaches a persistence port.
/// </summary>
internal static class PersonalAccessTokenSecretGenerator
{
	// 256 bits of entropy -- ample margin against brute force for a bearer credential with a
	// bounded maximum lifetime (Domain.Authorization.PersonalAccessTokenPolicy.MaxLifetime).
	private const int SecretByteLength = 32;
	private const string TokenPrefix = "jtpat_";

	/// <summary>Generates a new plaintext token and its stored hash.</summary>
	public static (string PlaintextToken, string TokenHash) Generate()
	{
		var secretBytes = RandomNumberGenerator.GetBytes(SecretByteLength);
		var plaintextToken = TokenPrefix + Convert.ToBase64String(secretBytes)
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');

		return (plaintextToken, Hash(plaintextToken));
	}

	/// <summary>Computes the stored hash for a presented plaintext token.</summary>
	public static string Hash(string plaintextToken)
	{
		ArgumentNullException.ThrowIfNull(plaintextToken);

		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken)));
	}
}
