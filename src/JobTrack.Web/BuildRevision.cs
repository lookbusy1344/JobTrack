namespace JobTrack.Web;

using System.Reflection;

/// <summary>
///     The project version and git revision this assembly was compiled from, for the login page's
///     header — so someone reporting a defect can say which build they saw it on without shell
///     access to the deployment. The revision is baked in at compile time by the
///     <c>SetSourceRevisionId</c> target in <c>JobTrack.Web.csproj</c>, which the SDK folds into
///     <see cref="AssemblyInformationalVersionAttribute" /> as <c>1.1.1+&lt;revision&gt;</c> — the
///     part before the <c>+</c> is the <c>&lt;Version&gt;</c> from <c>Directory.Build.props</c>.
///     <para>
///         It is <c>git describe</c> output, so it reads as <c>v1.2.0-14-gabc123def456</c> once a
///         release tag exists, as the bare abbreviated sha until one does, and with a <c>-dirty</c>
///         suffix when built from a modified working tree.
///     </para>
///     <para>
///         It names the revision the binary was *built* from, which an incremental rebuild after a
///         commit touching no source file will not refresh; a clean or CI build always will.
///     </para>
/// </summary>
internal static class BuildRevision
{
	private const char RevisionSeparator = '+';

	/// <summary>
	///     The abbreviated revision, or <c>null</c> where the build had no git repository to read one
	///     from (a source drop, or a machine with no git binary). Callers omit the display entirely
	///     rather than substituting a placeholder — a wrong or invented revision is worse than none.
	/// </summary>
	internal static string? Short { get; } = ReadRevision();

	/// <summary>
	///     The project's own <c>&lt;Version&gt;</c> (e.g. <c>1.1.1</c>) — the part of
	///     <see cref="AssemblyInformationalVersionAttribute" /> before the <see cref="RevisionSeparator" />.
	///     Distinct from <see cref="Short" />: this is the release number set in
	///     <c>Directory.Build.props</c>, not the <c>git describe</c> revision.
	/// </summary>
	internal static string? ProductVersion { get; } = ReadProductVersion();

	private static string? ReadInformationalVersion() =>
		typeof(BuildRevision).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

	private static string? ReadRevision()
	{
		var informationalVersion = ReadInformationalVersion();
		if (informationalVersion is null) {
			return null;
		}

		var separator = informationalVersion.IndexOf(RevisionSeparator, StringComparison.Ordinal);

		return separator >= 0 && separator < informationalVersion.Length - 1
			? informationalVersion[(separator + 1)..]
			: null;
	}

	private static string? ReadProductVersion()
	{
		var informationalVersion = ReadInformationalVersion();
		if (informationalVersion is null) {
			return null;
		}

		var separator = informationalVersion.IndexOf(RevisionSeparator, StringComparison.Ordinal);

		return separator >= 0 ? informationalVersion[..separator] : informationalVersion;
	}
}
