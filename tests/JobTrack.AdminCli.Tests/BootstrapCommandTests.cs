namespace JobTrack.AdminCli.Tests;

using Abstractions;
using AwesomeAssertions;

public sealed class BootstrapCommandTests
{
	[Fact]
	public async Task Throws_when_clearing_the_forced_password_change_without_a_user_manager()
	{
		var console = new FakeConsoleIO([], []);

		var act = async () => await BootstrapCommand.RunAsync(
			console, new FakeInstallationCommands(), "os-user", CancellationToken.None, forcePasswordChange: false);

		_ = await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task Prompts_for_every_field_in_order_and_reports_success()
	{
		var console = new FakeConsoleIO(
			["Ada Lovelace", "Europe/Paris", "ada.lovelace"],
			["correct-horse-battery-staple", "correct-horse-battery-staple"]);
		var installationCommands = new FakeInstallationCommands();

		var exitCode = await BootstrapCommand.RunAsync(console, installationCommands, "os-user", CancellationToken.None);

		exitCode.Should().Be(0);
		console.Prompts.Should().ContainInOrder(
			"Administrator display name: ", "IANA time zone [Europe/London]: ", "Username [os-user]: ", "Password: ", "Confirm password: ");
		installationCommands.ReceivedRequest.Should().NotBeNull();
		installationCommands.ReceivedRequest!.DisplayName.Should().Be("Ada Lovelace");
		installationCommands.ReceivedRequest.IanaTimeZone.Should().Be("Europe/Paris");
		installationCommands.ReceivedRequest.DefaultHourlyRate.Should().Be(new HourlyRate(20m));
		installationCommands.ReceivedRequest.UserName.Should().Be("ada.lovelace");
		installationCommands.ReceivedRequest.Password.Should().Be("correct-horse-battery-staple");
	}

	[Fact]
	public async Task Falls_back_to_the_UK_time_zone_and_the_current_OS_username_when_left_blank()
	{
		var console = new FakeConsoleIO(
			["Ada Lovelace", string.Empty, string.Empty],
			["correct-horse-battery-staple", "correct-horse-battery-staple"]);
		var installationCommands = new FakeInstallationCommands();

		var exitCode = await BootstrapCommand.RunAsync(console, installationCommands, "os-user", CancellationToken.None);

		exitCode.Should().Be(0);
		installationCommands.ReceivedRequest!.IanaTimeZone.Should().Be("Europe/London");
		installationCommands.ReceivedRequest.UserName.Should().Be("os-user");
	}

	[Fact]
	public async Task Reports_the_new_administrator_and_root_node_ids_without_the_password()
	{
		var console = new FakeConsoleIO(
			["Ada Lovelace", "Europe/London", "ada.lovelace"],
			["correct-horse-battery-staple", "correct-horse-battery-staple"]);

		_ = await BootstrapCommand.RunAsync(console, new FakeInstallationCommands(), "os-user", CancellationToken.None);

		console.Lines.Should().ContainSingle(line => line.Contains("Administrator id 1", StringComparison.Ordinal));
		console.Lines.Concat(console.Errors).Should().AllSatisfy(message => message.Should().NotContain("correct-horse-battery-staple"));
	}

	[Fact]
	public async Task Re_prompts_when_the_password_confirmation_does_not_match()
	{
		var console = new FakeConsoleIO(
			["Ada Lovelace", "Europe/London", "ada.lovelace"],
			["first-attempt", "does-not-match", "correct-horse-battery-staple", "correct-horse-battery-staple"]);
		var installationCommands = new FakeInstallationCommands();

		var exitCode = await BootstrapCommand.RunAsync(console, installationCommands, "os-user", CancellationToken.None);

		exitCode.Should().Be(0);
		installationCommands.ReceivedRequest!.Password.Should().Be("correct-horse-battery-staple");
		console.Errors.Should().Contain(error => error.Contains("did not match", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task Skips_the_password_prompt_when_a_password_is_supplied_non_interactively()
	{
		var console = new FakeConsoleIO(["Ada Lovelace", "Europe/London", "ada.lovelace"], []);
		var installationCommands = new FakeInstallationCommands();

		var exitCode = await BootstrapCommand.RunAsync(
			console, installationCommands, "os-user", CancellationToken.None, "correct-horse-battery-staple");

		exitCode.Should().Be(0);
		console.Prompts.Should().NotContain("Password: ");
		console.Prompts.Should().NotContain("Confirm password: ");
		installationCommands.ReceivedRequest!.Password.Should().Be("correct-horse-battery-staple");
	}

	[Fact]
	public async Task Reports_failure_without_a_stack_trace_when_already_initialised()
	{
		var console = new FakeConsoleIO(
			["Ada Lovelace", "Europe/London", "ada.lovelace"],
			["correct-horse-battery-staple", "correct-horse-battery-staple"]);

		var exitCode = await BootstrapCommand.RunAsync(console, new FakeInstallationCommands(true), "os-user", CancellationToken.None);

		exitCode.Should().Be(1);
		console.Errors.Should().ContainSingle(error => error.Contains("already initialised", StringComparison.OrdinalIgnoreCase));
	}
}
