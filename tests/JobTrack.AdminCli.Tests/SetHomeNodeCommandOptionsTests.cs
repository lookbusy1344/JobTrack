namespace JobTrack.AdminCli.Tests;

using AwesomeAssertions;
using PicoArgs_dotnet;

public sealed class SetHomeNodeCommandOptionsTests
{
	[Fact]
	public void Parses_provider_connection_string_username_and_node_id()
	{
		var options = SetHomeNodeCommandOptions.Parse(new(
			["--provider", "sqlite", "--connection-string", "Data Source=test.db", "--username", "ada.lovelace", "--node-id", "7"]));

		options.Provider.Should().Be(AdminCliProvider.Sqlite);
		options.ConnectionString.Should().Be("Data Source=test.db");
		options.Username.Should().Be("ada.lovelace");
		options.JobNodeId.Should().Be(7);
	}

	[Fact]
	public void Parses_clear_as_no_node_id()
	{
		var options = SetHomeNodeCommandOptions.Parse(new(
			["--provider", "sqlite", "--connection-string", "x", "--username", "ada", "--clear"]));

		options.JobNodeId.Should().BeNull();
	}

	[Fact]
	public void Rejects_neither_node_id_nor_clear()
	{
		var act = () => SetHomeNodeCommandOptions.Parse(new(["--provider", "sqlite", "--connection-string", "x", "--username", "ada"]));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_both_node_id_and_clear()
	{
		var act = () => SetHomeNodeCommandOptions.Parse(new(
			["--provider", "sqlite", "--connection-string", "x", "--username", "ada", "--node-id", "7", "--clear"]));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Theory]
	[InlineData("0")]
	[InlineData("-3")]
	[InlineData("not-a-number")]
	public void Rejects_a_node_id_that_is_not_a_positive_integer(string nodeId)
	{
		var act = () => SetHomeNodeCommandOptions.Parse(new(
			["--provider", "sqlite", "--connection-string", "x", "--username", "ada", "--node-id", nodeId]));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_unrecognised_extra_flag()
	{
		var act = () => SetHomeNodeCommandOptions.Parse(new(
			["--provider", "sqlite", "--connection-string", "x", "--username", "ada", "--node-id", "7", "--bogus", "value"]));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_missing_username_flag()
	{
		var act = () => SetHomeNodeCommandOptions.Parse(new(["--provider", "sqlite", "--connection-string", "x", "--node-id", "7"]));

		act.Should().Throw<PicoArgsException>();
	}
}
