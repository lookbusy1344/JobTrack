namespace JobTrack.ArchitectureTests;

using System.Text.RegularExpressions;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     ADR 0071 §7.2: passkey enrolment, removal, and reset are compound credential transitions —
///     they rotate the security/concurrency stamps, revoke tokens/sessions, and write audit — so a
///     Razor Page handler must drive them through the atomic <c>IJobTrackClient.Credentials</c>
///     commands, never the raw Identity store's <c>IUserPasskeyStore</c> mutators, which do none of
///     that. The framework's own ceremonies still call the store; this guard is scoped to page handlers.
/// </summary>
public sealed partial class PasskeyStoreBypassArchitectureTests
{
	[Fact]
	public void No_razor_page_handler_calls_the_identity_store_passkey_mutators_directly()
	{
		var webRoot = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web");
		var violations = new List<string>();

		foreach (var file in Directory.EnumerateFiles(webRoot, "*.cshtml.cs", SearchOption.AllDirectories)) {
			var content = File.ReadAllText(file);
			var relativePath = Path.GetRelativePath(RepositoryPaths.SolutionRoot(), file);

			// AddOrUpdatePasskeyAsync exists only on the store/UserManager: the atomic facade exposes
			// AddPasskeyAsync, so any call from a page handler is a bypass.
			if (AddOrUpdatePattern().IsMatch(content)) {
				violations.Add($"{relativePath}: calls AddOrUpdatePasskeyAsync");
			}

			// RemovePasskeyAsync is on both the store (bypass) and the facade (allowed); flag only calls
			// whose receiver is not the IJobTrackClient.Credentials facade.
			foreach (Match match in StoreRemovePattern().Matches(content)) {
				if (!string.Equals(match.Groups["receiver"].Value, "Credentials", StringComparison.Ordinal)) {
					violations.Add($"{relativePath}: calls {match.Groups["receiver"].Value}.RemovePasskeyAsync");
				}
			}
		}

		violations.Should().BeEmpty(
			"passkey removal and enrolment from a page handler must go through IJobTrackClient.Credentials so stamp " +
			"rotation, token revocation, and audit are never bypassed (ADR 0071 §7.2)");
	}

	[GeneratedRegex(@"\.AddOrUpdatePasskeyAsync\s*\(")]
	private static partial Regex AddOrUpdatePattern();

	[GeneratedRegex(@"(?<receiver>\w+)\.RemovePasskeyAsync\s*\(")]
	private static partial Regex StoreRemovePattern();
}
