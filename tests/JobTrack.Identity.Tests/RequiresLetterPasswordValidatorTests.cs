namespace JobTrack.Identity.Tests;

using AwesomeAssertions;

public sealed class RequiresLetterPasswordValidatorTests
{
	private readonly RequiresLetterPasswordValidator validator = new();

	private static JobTrackIdentityUser AnyUser() => new() {
		AppUserId = new(1),
		UserName = "user",
		NormalizedUserName = "USER",
		PasswordHash = string.Empty,
		SecurityStamp = "stamp",
		ConcurrencyStamp = "concurrency",
	};

	[Theory]
	[InlineData("abc123")]
	[InlineData("ABC123")]
	[InlineData("a1B2c3")]
	public async Task Succeeds_when_the_password_contains_a_letter_of_either_case(string password)
	{
		var result = await validator.ValidateAsync(null!, AnyUser(), password);

		result.Succeeded.Should().BeTrue();
	}

	[Fact]
	public async Task Fails_when_the_password_has_no_letters()
	{
		var result = await validator.ValidateAsync(null!, AnyUser(), "123456");

		result.Succeeded.Should().BeFalse();
		result.Errors.Should().ContainSingle(error => error.Code == "PasswordRequiresLetter");
	}

	[Fact]
	public async Task Fails_when_the_password_is_null()
	{
		var result = await validator.ValidateAsync(null!, AnyUser(), null);

		result.Succeeded.Should().BeFalse();
	}
}
