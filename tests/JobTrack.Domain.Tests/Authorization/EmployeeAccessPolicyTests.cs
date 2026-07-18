namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class EmployeeAccessPolicyTests
{
	private static readonly AppUserId Actor = new(1);
	private static readonly AppUserId OtherEmployee = new(2);

	[Fact]
	public void An_actor_may_view_their_own_profile_with_no_roles()
	{
		var canView = EmployeeAccessPolicy.CanViewEmployee(Actor, Actor, []);

		canView.Should().BeTrue();
	}

	[Fact]
	public void An_administrator_may_view_another_employee()
	{
		var canView = EmployeeAccessPolicy.CanViewEmployee(Actor, OtherEmployee, [EmployeeRole.Administrator]);

		canView.Should().BeTrue();
	}

	[Fact]
	public void A_non_administrator_may_not_view_another_employee()
	{
		var canView = EmployeeAccessPolicy.CanViewEmployee(Actor, OtherEmployee, [EmployeeRole.Worker]);

		canView.Should().BeFalse();
	}

	[Fact]
	public void An_actor_with_no_roles_may_not_view_another_employee()
	{
		var canView = EmployeeAccessPolicy.CanViewEmployee(Actor, OtherEmployee, []);

		canView.Should().BeFalse();
	}

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => EmployeeAccessPolicy.CanViewEmployee(Actor, OtherEmployee, null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void An_administrator_may_manage_role_assignments()
	{
		var canManage = EmployeeAccessPolicy.CanManageRoles([EmployeeRole.Administrator]);

		canManage.Should().BeTrue();
	}

	[Fact]
	public void A_non_administrator_may_not_manage_role_assignments()
	{
		var canManage = EmployeeAccessPolicy.CanManageRoles([EmployeeRole.JobManager]);

		canManage.Should().BeFalse();
	}

	[Fact]
	public void An_actor_with_no_roles_may_not_manage_role_assignments()
	{
		var canManage = EmployeeAccessPolicy.CanManageRoles([]);

		canManage.Should().BeFalse();
	}

	[Fact]
	public void A_null_role_collection_is_rejected_for_role_management()
	{
		var act = () => EmployeeAccessPolicy.CanManageRoles(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void An_administrator_may_manage_accounts()
	{
		var canManage = EmployeeAccessPolicy.CanManageAccounts([EmployeeRole.Administrator]);

		canManage.Should().BeTrue();
	}

	[Fact]
	public void A_non_administrator_may_not_manage_accounts()
	{
		var canManage = EmployeeAccessPolicy.CanManageAccounts([EmployeeRole.JobManager]);

		canManage.Should().BeFalse();
	}

	[Fact]
	public void An_actor_with_no_roles_may_not_manage_accounts()
	{
		var canManage = EmployeeAccessPolicy.CanManageAccounts([]);

		canManage.Should().BeFalse();
	}

	[Fact]
	public void A_null_role_collection_is_rejected_for_account_management()
	{
		var act = () => EmployeeAccessPolicy.CanManageAccounts(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
