namespace JobTrack.AdminCli.Tests;

using Abstractions;
using AwesomeAssertions;
using PicoArgs_dotnet;

public sealed class CreateEmployeeCommandOptionsTests
{
	private static string[] Required(string roles = "JobManager", params string[] extra) => [
		"--provider", "sqlite",
		"--connection-string", "Data Source=test.db",
		"--actor", "admin",
		"--username", "demo",
		"--password", "demo1234",
		"--display-name", "Demo User",
		"--roles", roles,
		.. extra,
	];

	[Fact]
	public void Parses_all_required_fields()
	{
		var options = CreateEmployeeCommandOptions.Parse(new(Required()));

		options.Provider.Should().Be(AdminCliProvider.Sqlite);
		options.ConnectionString.Should().Be("Data Source=test.db");
		options.ActorUsername.Should().Be("admin");
		options.Username.Should().Be("demo");
		options.Password.Should().Be("demo1234");
		options.DisplayName.Should().Be("Demo User");
		options.Roles.Should().Equal(EmployeeRole.JobManager);
	}

	[Fact]
	public void Defaults_the_time_zone_and_forces_a_password_change()
	{
		var options = CreateEmployeeCommandOptions.Parse(new(Required()));

		options.IanaTimeZone.Should().Be("Europe/London");
		options.ForcePasswordChange.Should().BeTrue();
		options.DefaultHourlyRate.Should().BeNull();
	}

	[Fact]
	public void Parses_multiple_comma_separated_roles_in_order()
	{
		var options = CreateEmployeeCommandOptions.Parse(new(Required("JobManager,Worker")));

		options.Roles.Should().Equal(EmployeeRole.JobManager, EmployeeRole.Worker);
	}

	[Fact]
	public void Parses_roles_case_insensitively_and_trims_whitespace()
	{
		var options = CreateEmployeeCommandOptions.Parse(new(Required(" jobmanager , worker ")));

		options.Roles.Should().Equal(EmployeeRole.JobManager, EmployeeRole.Worker);
	}

	[Fact]
	public void Clears_the_forced_password_change_when_flagged()
	{
		var options = CreateEmployeeCommandOptions.Parse(new(Required("JobManager", "--no-force-password-change")));

		options.ForcePasswordChange.Should().BeFalse();
	}

	[Fact]
	public void Parses_an_optional_default_hourly_rate()
	{
		var options = CreateEmployeeCommandOptions.Parse(new(Required("JobManager", "--default-hourly-rate", "25.50")));

		options.DefaultHourlyRate.Should().Be(25.50m);
	}

	[Fact]
	public void Parses_the_postgresql_provider()
	{
		var args = Required();
		args[1] = "postgresql";
		args[3] = "Host=localhost";

		var options = CreateEmployeeCommandOptions.Parse(new(args));

		options.Provider.Should().Be(AdminCliProvider.PostgreSql);
	}

	[Fact]
	public void Rejects_an_empty_roles_list()
	{
		var act = () => CreateEmployeeCommandOptions.Parse(new(Required("")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_unknown_role()
	{
		var act = () => CreateEmployeeCommandOptions.Parse(new(Required("Wizard")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_the_none_role()
	{
		var act = () => CreateEmployeeCommandOptions.Parse(new(Required("None")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_a_non_numeric_default_hourly_rate()
	{
		var act = () => CreateEmployeeCommandOptions.Parse(new(Required("JobManager", "--default-hourly-rate", "free")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_unknown_provider()
	{
		var args = Required();
		args[1] = "mysql";

		var act = () => CreateEmployeeCommandOptions.Parse(new(args));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_unrecognised_extra_flag()
	{
		var act = () => CreateEmployeeCommandOptions.Parse(new(Required("JobManager", "--bogus", "value")));

		act.Should().Throw<PicoArgsException>();
	}

	[Fact]
	public void Rejects_a_missing_actor()
	{
		var act = () => CreateEmployeeCommandOptions.Parse(
			new([
				"--provider", "sqlite", "--connection-string", "x", "--username", "demo",
				"--password", "p", "--display-name", "D", "--roles", "Worker",
			]));

		act.Should().Throw<PicoArgsException>();
	}
}
