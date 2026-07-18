namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class AchievementAccessPolicyTests
{
	[Fact]
	public void An_owning_worker_may_make_an_ordinary_forward_transition()
	{
		AchievementAccessPolicy
			.CanSetAchievement([EmployeeRole.Worker], true, false)
			.Should().BeTrue();
	}

	[Fact]
	public void A_non_owning_worker_may_not_make_an_ordinary_forward_transition()
	{
		AchievementAccessPolicy
			.CanSetAchievement([EmployeeRole.Worker], false, false)
			.Should().BeFalse();
	}

	[Fact]
	public void An_owning_worker_may_not_reopen_a_terminal_state()
	{
		AchievementAccessPolicy
			.CanSetAchievement([EmployeeRole.Worker], true, true)
			.Should().BeFalse();
	}

	[Fact]
	public void A_job_manager_may_reopen_a_terminal_state_they_do_not_own()
	{
		AchievementAccessPolicy
			.CanSetAchievement([EmployeeRole.JobManager], false, true)
			.Should().BeTrue();
	}

	[Fact]
	public void An_administrator_may_reopen_a_terminal_state_they_do_not_own()
	{
		AchievementAccessPolicy
			.CanSetAchievement([EmployeeRole.Administrator], false, true)
			.Should().BeTrue();
	}

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => AchievementAccessPolicy.CanSetAchievement(null!, true, false);

		act.Should().Throw<ArgumentNullException>();
	}
}
