namespace JobTrack.AdminCli.Tests;

using AwesomeAssertions;
using NodaTime;
using PicoArgs_dotnet;

public sealed class SetScheduleCommandOptionsTests
{
	private static string[] BaseArgs(params string[] extra) =>
	[
		"--provider", "sqlite", "--connection-string", "Data Source=test.db",
		"--actor", "admin", "--username", "ada.lovelace", .. extra,
	];

	[Fact]
	public void Parses_provider_connection_string_actor_username_days_and_times()
	{
		var options = SetScheduleCommandOptions.Parse(new(
			BaseArgs("--days", "Mon,Tue,Wed,Thu,Fri", "--start", "09:00", "--end", "17:00")));

		options.Provider.Should().Be(AdminCliProvider.Sqlite);
		options.ConnectionString.Should().Be("Data Source=test.db");
		options.ActorUsername.Should().Be("admin");
		options.Username.Should().Be("ada.lovelace");
		options.Days.Should().Equal(
			IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday);
		options.Start.Should().Be(new LocalTime(9, 0));
		options.End.Should().Be(new LocalTime(17, 0));
	}

	[Theory]
	[InlineData("Monday")]
	[InlineData("monday")]
	[InlineData("MON")]
	[InlineData("mon")]
	public void Accepts_a_day_by_full_or_abbreviated_name_in_any_case(string day)
	{
		var options = SetScheduleCommandOptions.Parse(new(BaseArgs("--days", day, "--start", "09:00", "--end", "17:00")));

		options.Days.Should().Equal(IsoDayOfWeek.Monday);
	}

	[Fact]
	public void Parses_every_day_of_the_week()
	{
		var options = SetScheduleCommandOptions.Parse(new(
			BaseArgs("--days", "Mon,Tue,Wed,Thu,Fri,Sat,Sun", "--start", "08:00", "--end", "20:00")));

		options.Days.Should().HaveCount(7).And.Contain(IsoDayOfWeek.Sunday).And.Contain(IsoDayOfWeek.Saturday);
	}

	[Fact]
	public void Defaults_the_time_zone_to_Europe_London()
	{
		var options = SetScheduleCommandOptions.Parse(new(BaseArgs("--days", "Mon", "--start", "09:00", "--end", "17:00")));

		options.IanaTimeZone.Should().Be("Europe/London");
	}

	[Fact]
	public void Parses_an_explicit_time_zone_and_effective_start()
	{
		var options = SetScheduleCommandOptions.Parse(new(BaseArgs(
			"--days", "Mon", "--start", "09:00", "--end", "17:00",
			"--iana-time-zone", "America/New_York", "--effective-start", "2026-03-01")));

		options.IanaTimeZone.Should().Be("America/New_York");
		options.EffectiveStart.Should().Be(new LocalDate(2026, 3, 1));
	}

	[Fact]
	public void Leaves_the_effective_start_unset_when_not_given()
	{
		var options = SetScheduleCommandOptions.Parse(new(BaseArgs("--days", "Mon", "--start", "09:00", "--end", "17:00")));

		options.EffectiveStart.Should().BeNull();
	}

	[Fact]
	public void Accepts_a_whole_second_time()
	{
		var options = SetScheduleCommandOptions.Parse(new(BaseArgs("--days", "Mon", "--start", "09:00:30", "--end", "17:00:45")));

		options.Start.Should().Be(new LocalTime(9, 0, 30));
		options.End.Should().Be(new LocalTime(17, 0, 45));
	}

	[Theory]
	[InlineData("Funday")]
	[InlineData("Mon,Funday")]
	[InlineData("")]
	[InlineData(",")]
	public void Rejects_an_unrecognised_or_empty_day_list(string days)
	{
		var act = () => SetScheduleCommandOptions.Parse(new(BaseArgs("--days", days, "--start", "09:00", "--end", "17:00")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_a_duplicated_day()
	{
		var act = () => SetScheduleCommandOptions.Parse(new(BaseArgs("--days", "Mon,Mon", "--start", "09:00", "--end", "17:00")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Theory]
	[InlineData("9am")]
	[InlineData("25:00")]
	[InlineData("not-a-time")]
	public void Rejects_a_time_that_is_not_a_24_hour_clock_value(string start)
	{
		var act = () => SetScheduleCommandOptions.Parse(new(BaseArgs("--days", "Mon", "--start", start, "--end", "17:00")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_a_sub_second_time()
	{
		var act = () => SetScheduleCommandOptions.Parse(new(BaseArgs("--days", "Mon", "--start", "09:00:00.5", "--end", "17:00")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_end_equal_to_the_start()
	{
		var act = () => SetScheduleCommandOptions.Parse(new(BaseArgs("--days", "Mon", "--start", "09:00", "--end", "09:00")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_effective_start_that_is_not_a_date()
	{
		var act = () => SetScheduleCommandOptions.Parse(new(
			BaseArgs("--days", "Mon", "--start", "09:00", "--end", "17:00", "--effective-start", "01/03/2026")));

		act.Should().Throw<AdminCliUsageException>();
	}

	[Fact]
	public void Rejects_an_unrecognised_extra_flag()
	{
		var act = () => SetScheduleCommandOptions.Parse(new(
			BaseArgs("--days", "Mon", "--start", "09:00", "--end", "17:00", "--bogus", "value")));

		act.Should().Throw<PicoArgsException>();
	}

	[Theory]
	[InlineData("--days")]
	[InlineData("--start")]
	[InlineData("--end")]
	[InlineData("--actor")]
	[InlineData("--username")]
	public void Rejects_a_missing_required_flag(string missing)
	{
		string[] all = [
			"--provider", "sqlite", "--connection-string", "x", "--actor", "admin", "--username", "ada",
			"--days", "Mon", "--start", "09:00", "--end", "17:00",
		];
		var index = Array.IndexOf(all, missing);
		string[] args = [.. all[..index], .. all[(index + 2)..]];

		var act = () => SetScheduleCommandOptions.Parse(new(args));

		act.Should().Throw<PicoArgsException>();
	}
}
