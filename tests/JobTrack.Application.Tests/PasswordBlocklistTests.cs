namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;

public sealed class PasswordBlocklistTests
{
	[Fact]
	public void Contains_is_false_for_null_or_empty()
	{
		PasswordBlocklist.Contains(null).Should().BeFalse();
		PasswordBlocklist.Contains(string.Empty).Should().BeFalse();
	}

	[Theory]
	[InlineData("JobTrack")]
	[InlineData("jobtrack")]
	[InlineData("JOBTRACK")]
	public void Contains_blocks_the_product_name_regardless_of_case(string password)
	{
		PasswordBlocklist.Contains(password).Should().BeTrue();
	}

	[Theory]
	[InlineData("grace.hopper")]
	[InlineData("GRACE.HOPPER")]
	public void Contains_blocks_the_supplied_username_regardless_of_case(string password)
	{
		PasswordBlocklist.Contains(password, "grace.hopper").Should().BeTrue();
	}

	[Fact]
	public void Contains_does_not_block_an_unrelated_password_against_a_username()
	{
		PasswordBlocklist.Contains("a-completely-different-passphrase", "grace.hopper").Should().BeFalse();
	}

	[Fact]
	public void Contains_blocks_a_known_common_password()
	{
		PasswordBlocklist.Contains("correcthorsebatterystaple").Should().BeTrue();
	}

	[Fact]
	public void Contains_does_not_block_a_passphrase_that_merely_contains_a_blocked_value()
	{
		// Exact match only -- a substring match would over-block ordinary long passphrases that
		// happen to contain a common word.
		PasswordBlocklist.Contains("myjobtrackpasswordisverylongandunique").Should().BeFalse();
	}

	[Fact]
	public void Contains_returns_false_for_an_unlisted_strong_password()
	{
		PasswordBlocklist.Contains("a genuinely unusual passphrase 42").Should().BeFalse();
	}
}
