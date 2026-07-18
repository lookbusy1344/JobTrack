namespace JobTrack.AdminCli.Tests;

using AwesomeAssertions;
using PicoArgs_dotnet;

public sealed class ResetPasswordCommandOptionsTests
{
	[Fact]
	public void Parses_provider_connection_string_and_username()
	{
		var options = ResetPasswordCommandOptions.Parse(new(
			["--provider", "sqlite", "--connection-string", "Data Source=test.db", "--username", "ada.lovelace"]));

		options.Provider.Should().Be(AdminCliProvider.Sqlite);
		options.ConnectionString.Should().Be("Data Source=test.db");
		options.Username.Should().Be("ada.lovelace");
	}

	[Fact]
	public void Rejects_an_unrecognised_extra_flag()
	{
		var act = () => ResetPasswordCommandOptions.Parse(new(
			["--provider", "sqlite", "--connection-string", "x", "--username", "ada", "--bogus", "value"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_flag_with_a_missing_value()
	{
		var act = () => ResetPasswordCommandOptions.Parse(new(["--username"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_missing_provider_flag()
	{
		var act = () => ResetPasswordCommandOptions.Parse(new(["--connection-string", "x", "--username", "ada"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_missing_connection_string_flag()
	{
		var act = () => ResetPasswordCommandOptions.Parse(new(["--provider", "sqlite", "--username", "ada"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_missing_username_flag()
	{
		var act = () => ResetPasswordCommandOptions.Parse(new(["--provider", "sqlite", "--connection-string", "x"]));

		act.Should().Throw<PicoArgsException>();
	}
}
