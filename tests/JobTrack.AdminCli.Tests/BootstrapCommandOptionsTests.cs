namespace JobTrack.AdminCli.Tests;

using AwesomeAssertions;
using PicoArgs_dotnet;

public sealed class BootstrapCommandOptionsTests
{
	[Fact]
	public void Parses_provider_and_connection_string()
	{
		var options = BootstrapCommandOptions.Parse(new(["--provider", "sqlite", "--connection-string", "Data Source=test.db"]));

		options.Provider.Should().Be(AdminCliProvider.Sqlite);
		options.ConnectionString.Should().Be("Data Source=test.db");
	}

	[Fact]
	public void Parses_an_optional_password()
	{
		var options = BootstrapCommandOptions.Parse(
			new(["--provider", "sqlite", "--connection-string", "Data Source=test.db", "--password", "correct-horse-battery-staple"]));

		options.Password.Should().Be("correct-horse-battery-staple");
	}

	[Fact]
	public void Leaves_password_null_when_omitted()
	{
		var options = BootstrapCommandOptions.Parse(new(["--provider", "sqlite", "--connection-string", "Data Source=test.db"]));

		options.Password.Should().BeNull();
	}

	[Fact]
	public void Forces_a_password_change_by_default()
	{
		var options = BootstrapCommandOptions.Parse(new(["--provider", "sqlite", "--connection-string", "Data Source=test.db"]));

		options.ForcePasswordChange.Should().BeTrue();
	}

	[Fact]
	public void Clears_the_forced_password_change_when_flagged()
	{
		var options = BootstrapCommandOptions.Parse(
			new(["--provider", "sqlite", "--connection-string", "Data Source=test.db", "--no-force-password-change"]));

		options.ForcePasswordChange.Should().BeFalse();
	}

	[Fact]
	public void Parses_postgresql_provider()
	{
		var options = BootstrapCommandOptions.Parse(new(["--provider", "postgresql", "--connection-string", "Host=localhost"]));

		options.Provider.Should().Be(AdminCliProvider.PostgreSql);
	}

	[Fact]
	public void Rejects_an_unknown_provider()
	{
		var act = () => BootstrapCommandOptions.Parse(new(["--provider", "mysql", "--connection-string", "x"]));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_unrecognised_extra_flag()
	{
		var act = () => BootstrapCommandOptions.Parse(
			new(["--provider", "sqlite", "--connection-string", "x", "--bogus", "value"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_flag_with_a_missing_value()
	{
		var act = () => BootstrapCommandOptions.Parse(new(["--provider"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_missing_provider_flag()
	{
		var act = () => BootstrapCommandOptions.Parse(new(["--connection-string", "x"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_missing_connection_string_flag()
	{
		var act = () => BootstrapCommandOptions.Parse(new(["--provider", "sqlite"]));

		act.Should().Throw<PicoArgsException>();
	}
}
