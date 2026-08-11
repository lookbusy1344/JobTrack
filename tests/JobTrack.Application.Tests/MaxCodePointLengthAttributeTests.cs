namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;

public sealed class MaxCodePointLengthAttributeTests
{
	[Fact]
	public void IsValid_accepts_null()
	{
		var attribute = new MaxCodePointLengthAttribute(4);

		attribute.IsValid(null).Should().BeTrue();
	}

	[Fact]
	public void IsValid_accepts_a_non_string_value()
	{
		var attribute = new MaxCodePointLengthAttribute(4);

		attribute.IsValid(42).Should().BeTrue();
	}

	[Fact]
	public void IsValid_accepts_text_exactly_at_the_maximum()
	{
		var attribute = new MaxCodePointLengthAttribute(4);

		attribute.IsValid("abcd").Should().BeTrue();
	}

	[Fact]
	public void IsValid_rejects_text_longer_than_the_maximum()
	{
		var attribute = new MaxCodePointLengthAttribute(4);

		attribute.IsValid("abcde").Should().BeFalse();
	}

	[Fact]
	public void IsValid_counts_unicode_code_points_not_utf16_code_units()
	{
		// Two emoji surrogate pairs -- 4 UTF-16 code units, 2 Rune/code points -- must pass a
		// maximum of 2, which a raw string.Length check would wrongly reject.
		var attribute = new MaxCodePointLengthAttribute(2);
		var text = "🔥🔥";

		text.Length.Should().Be(4, "each emoji here is a surrogate pair");
		attribute.IsValid(text).Should().BeTrue();
	}

	[Fact]
	public void IsValid_rejects_text_over_the_maximum_measured_in_code_points()
	{
		var attribute = new MaxCodePointLengthAttribute(2);
		var text = "🔥🔥🔥";

		attribute.IsValid(text).Should().BeFalse();
	}
}
