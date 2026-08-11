namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class PersonalAccessTokenAccessPolicyTests
{
	private static readonly AppUserId Alice = new(1);
	private static readonly AppUserId Bob = new(2);

	[Fact]
	public void An_actor_may_issue_a_token_for_themselves() => PersonalAccessTokenAccessPolicy.CanIssue(Alice, Alice).Should().BeTrue();

	[Fact]
	public void An_actor_may_not_issue_a_token_for_another_user() => PersonalAccessTokenAccessPolicy.CanIssue(Alice, Bob).Should().BeFalse();

	[Fact]
	public void An_actor_may_manage_their_own_tokens_even_without_the_administrator_role()
	{
		// Kills the CanManage `||`->`&&` and `==`->`!=` mutants: self-management must hold
		// on the identity branch alone, independent of the administrator branch.
		PersonalAccessTokenAccessPolicy.CanManage(Alice, Alice, [EmployeeRole.Worker]).Should().BeTrue();
	}

	[Fact]
	public void An_administrator_may_manage_another_users_tokens() =>
		PersonalAccessTokenAccessPolicy.CanManage(Alice, Bob, [EmployeeRole.Administrator]).Should().BeTrue();

	[Fact]
	public void A_non_administrator_may_not_manage_another_users_tokens() =>
		PersonalAccessTokenAccessPolicy.CanManage(Alice, Bob, [EmployeeRole.Worker]).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected_before_the_self_short_circuit()
	{
		// Actor == target would short-circuit past a removed guard and return true, so passing a
		// self actor pins that the null check fires first (kills the guard-removal mutant, which the
		// non-self path could not distinguish because Enumerable.Contains throws on null anyway).
		var act = () => PersonalAccessTokenAccessPolicy.CanManage(Alice, Alice, null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
