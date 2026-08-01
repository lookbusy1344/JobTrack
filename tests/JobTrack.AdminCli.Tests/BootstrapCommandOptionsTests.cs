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
	public void Rejects_a_password_supplied_in_process_arguments_without_echoing_it()
	{
		const string secret = "argv-must-not-contain-this-secret";
		var act = () => BootstrapCommandOptions.Parse(
			new(["--provider", "sqlite", "--connection-string", "Data Source=test.db", "--password", secret]));

		act.Should().Throw<AdminCliUsageException>().Which.Message.Should().NotContain(secret);
	}

	[Fact]
	public void Rejects_a_direct_connection_string_containing_a_password_without_echoing_it()
	{
		const string secret = "database-secret-in-argv";
		var act = () => BootstrapCommandOptions.Parse(
			new(["--provider", "postgresql", "--connection-string", $"Host=localhost;Password={secret}"]));

		act.Should().Throw<AdminCliUsageException>().Which.Message.Should().NotContain(secret);
	}

	[Fact]
	public void Rejects_the_pwd_connection_string_alias()
	{
		var act = () => BootstrapCommandOptions.Parse(
			new(["--provider", "postgresql", "--connection-string", "Host=localhost;Pwd=secret"]));

		act.Should().Throw<AdminCliUsageException>();
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

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Parses_a_connection_string_file_flag()
	{
		var path = Path.GetTempFileName();
		try {
			File.WriteAllText(path, "Data Source=test.db\n");

			var options = BootstrapCommandOptions.Parse(new(["--provider", "sqlite", "--connection-string-file", path]));

			options.ConnectionString.Should().Be("Data Source=test.db");
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void Rejects_both_connection_string_and_connection_string_file()
	{
		var act = () => BootstrapCommandOptions.Parse(
			new(["--provider", "sqlite", "--connection-string", "x", "--connection-string-file", "/tmp/does-not-matter"]));

		act.Should().Throw<AdminCliUsageException>().WithMessage("*mutually exclusive*");
	}

	[Fact]
	public void Sets_password_from_stdin_flag()
	{
		var options = BootstrapCommandOptions.Parse(
			new(["--provider", "sqlite", "--connection-string", "x", "--password-stdin"]));

		options.PasswordFromStdin.Should().BeTrue();
		options.Password.Should().BeNull();
	}

	[Fact]
	public void Rejects_password_even_when_password_stdin_is_also_present()
	{
		var act = () => BootstrapCommandOptions.Parse(
			new(["--provider", "sqlite", "--connection-string", "x", "--password", "secret", "--password-stdin"]));

		act.Should().Throw<AdminCliUsageException>().WithMessage("*not supported*");
	}
}
