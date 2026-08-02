namespace JobTrack.Abstractions;

/// <summary>
///     Unicode-code-point counting for user-facing text limits. <see cref="string.Length" /> counts
///     UTF-16 code units, so a surrogate-pair character (most emoji) counts as 2 instead of 1 --
///     wrong for any limit meant to bound how much text a person entered. Every character/length
///     limit on user-facing text should measure through this, not <see cref="string.Length" />.
/// </summary>
public static class TextLength
{
	/// <summary>The number of Unicode code points (<see cref="System.Text.Rune" />) in <paramref name="value" />; zero for null.</summary>
	public static int CodePointCount(string? value) => value is null ? 0 : value.EnumerateRunes().Count();
}
