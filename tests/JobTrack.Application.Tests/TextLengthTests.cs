namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;

public sealed class TextLengthTests
{
	[Fact]
	public void CodePointCount_returns_zero_for_null() => TextLength.CodePointCount(null).Should().Be(0);

	[Fact]
	public void CodePointCount_returns_zero_for_empty_string() => TextLength.CodePointCount(string.Empty).Should().Be(0);

	[Fact]
	public void CodePointCount_matches_Length_for_ascii_text() => TextLength.CodePointCount("hello world").Should().Be("hello world".Length);

	[Fact]
	public void CodePointCount_counts_a_surrogate_pair_as_one_code_point()
	{
		// A single emoji is a surrogate pair -- 2 UTF-16 code units, 1 Rune/code point.
		var text = string.Concat(Enumerable.Repeat("🔥", 15));

		text.Length.Should().Be(30, "each emoji here is a surrogate pair");
		TextLength.CodePointCount(text).Should().Be(15);
	}
}
