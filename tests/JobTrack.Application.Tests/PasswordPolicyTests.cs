namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;

public sealed class PasswordPolicyTests
{
	[Fact]
	public void IsSatisfiedBy_rejects_a_null_password() => PasswordPolicy.IsSatisfiedBy(null).Should().BeFalse();

	[Theory]
	[InlineData("")]
	[InlineData("short-pass1")]
	[InlineData("fourteen-chars")]
	public void IsSatisfiedBy_rejects_a_password_shorter_than_the_minimum(string password) =>
		PasswordPolicy.IsSatisfiedBy(password).Should().BeFalse();

	[Fact]
	public void IsSatisfiedBy_accepts_a_password_exactly_at_the_minimum_length()
	{
		var password = new string('a', PasswordPolicy.MinimumLength);

		PasswordPolicy.IsSatisfiedBy(password).Should().BeTrue();
	}

	[Fact]
	public void IsSatisfiedBy_accepts_a_password_exactly_at_the_maximum_length()
	{
		var password = new string('a', PasswordPolicy.MaximumLength);

		PasswordPolicy.IsSatisfiedBy(password).Should().BeTrue();
	}

	[Fact]
	public void IsSatisfiedBy_rejects_a_password_longer_than_the_maximum()
	{
		var password = new string('a', PasswordPolicy.MaximumLength + 1);

		PasswordPolicy.IsSatisfiedBy(password).Should().BeFalse();
	}

	[Fact]
	public void IsSatisfiedBy_accepts_a_password_with_spaces_and_no_digit_or_letter_case_mix() =>
		PasswordPolicy.IsSatisfiedBy("a passphrase of only lowercase words").Should().BeTrue();

	[Fact]
	public void IsSatisfiedBy_counts_unicode_code_points_not_utf16_code_units()
	{
		// Each of these 15 emoji is a surrogate pair (2 UTF-16 code units, 1 Rune/code point each) --
		// a UTF-16-length check would see 30 and wrongly treat this as more than 2x the minimum.
		var password = string.Concat(Enumerable.Repeat("🔥", PasswordPolicy.MinimumLength - 1));

		password.Length.Should().Be((PasswordPolicy.MinimumLength - 1) * 2, "each emoji here is a surrogate pair");
		PasswordPolicy.IsSatisfiedBy(password).Should().BeFalse("14 code points is one short of the minimum");
	}
}
