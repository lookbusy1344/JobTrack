namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Identity;

public sealed class EmployeeCommandsTests
{
	private static readonly AppUserId AdministratorId = new(1);
	private static readonly AppUserId WorkerId = new(2);

	private static FakeEmployeeCommandPort CreateSeededPort()
	{
		var port = new FakeEmployeeCommandPort();
		port.SeedRoles(AdministratorId, EmployeeRole.Administrator);
		port.SeedRoles(WorkerId, EmployeeRole.Worker);

		return port;
	}

	private static EmployeeCommands CreateSut(FakeEmployeeCommandPort port) =>
		new(port, new PasswordHasher<EmployeeCredentialSubject>());

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	[Fact]
	public async Task An_administrator_can_create_a_new_employee()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);

		var result = await sut.CreateEmployeeAsync(new() {
			Context = ContextFor(AdministratorId),
			DisplayName = "Grace Hopper",
			IanaTimeZone = "Etc/UTC",
			UserName = "grace.hopper",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		result.UserName.Should().Be("grace.hopper");
		result.RequiresPasswordChange.Should().BeTrue();
		result.IsEnabled.Should().BeTrue();
		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
		port.LastCreateRequest!.DefaultHourlyRate.Should().Be(new HourlyRate(20m));
	}

	[Fact]
	public async Task CreateEmployeeAsync_preserves_an_explicit_default_hourly_rate()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);

		_ = await sut.CreateEmployeeAsync(new() {
			Context = ContextFor(AdministratorId),
			DisplayName = "Grace Hopper",
			IanaTimeZone = "Etc/UTC",
			UserName = "grace.hopper",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
			DefaultHourlyRate = new HourlyRate(25m),
		});

		port.LastCreateRequest!.DefaultHourlyRate.Should().Be(new HourlyRate(25m));
	}

	[Fact]
	public void Creating_an_employee_rejects_none_as_the_initial_role_synchronously()
	{
		var sut = CreateSut(CreateSeededPort());
		var request = new CreateEmployeeRequest {
			Context = ContextFor(AdministratorId),
			DisplayName = "Grace Hopper",
			IanaTimeZone = "Etc/UTC",
			UserName = "grace.hopper",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.None,
		};

		Action act = () => _ = sut.CreateEmployeeAsync(request);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task A_worker_cannot_create_a_new_employee()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.CreateEmployeeAsync(new() {
			Context = ContextFor(WorkerId),
			DisplayName = "Grace Hopper",
			IanaTimeZone = "Etc/UTC",
			UserName = "grace.hopper",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_can_set_an_employees_default_hourly_rate()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.SetDefaultHourlyRateAsync(new() {
			Context = ContextFor(AdministratorId),
			TargetUserId = WorkerId,
			DefaultHourlyRate = new(30m),
		});

		result.Id.Should().Be(WorkerId);
		result.DefaultHourlyRate.Should().Be(new HourlyRate(30m));
	}

	[Fact]
	public async Task A_worker_cannot_set_an_employees_default_hourly_rate()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () =>
			sut.SetDefaultHourlyRateAsync(new() { Context = ContextFor(WorkerId), TargetUserId = WorkerId, DefaultHourlyRate = new(30m) });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task SetDefaultHourlyRateAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.SetDefaultHourlyRateAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task Creating_an_employee_with_a_taken_username_throws_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);
		_ = await sut.CreateEmployeeAsync(new() {
			Context = ContextFor(AdministratorId),
			DisplayName = "Grace Hopper",
			IanaTimeZone = "Etc/UTC",
			UserName = "grace.hopper",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		var act = () => sut.CreateEmployeeAsync(new() {
			Context = ContextFor(AdministratorId),
			DisplayName = "Grace Hopper Duplicate",
			IanaTimeZone = "Etc/UTC",
			UserName = "grace.hopper",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		await act.Should().ThrowAsync<InvariantViolationException>();
	}

	[Fact]
	public async Task CreateEmployeeAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.CreateEmployeeAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task An_administrator_can_assign_a_role()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.AssignRoleAsync(new() {
			Context = ContextFor(AdministratorId),
			TargetUserId = WorkerId,
			Role = EmployeeRole.RateManager,
		});

		result.UserId.Should().Be(WorkerId);
		result.Roles.Should().Contain([EmployeeRole.Worker, EmployeeRole.RateManager]);
	}

	[Fact]
	public void Assigning_a_role_rejects_none_synchronously()
	{
		var sut = CreateSut(CreateSeededPort());
		var request = new AssignEmployeeRoleRequest { Context = ContextFor(AdministratorId), TargetUserId = WorkerId, Role = EmployeeRole.None };

		Action act = () => _ = sut.AssignRoleAsync(request);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task Assigning_an_already_held_role_is_idempotent()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.AssignRoleAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = WorkerId, Role = EmployeeRole.Worker });

		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
	}

	[Fact]
	public async Task A_worker_cannot_assign_a_role()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.AssignRoleAsync(new() { Context = ContextFor(WorkerId), TargetUserId = WorkerId, Role = EmployeeRole.RateManager });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Assigning_a_role_to_a_nonexistent_employee_throws_not_found()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.AssignRoleAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = new(999), Role = EmployeeRole.Worker });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task An_administrator_can_revoke_a_role()
	{
		var port = CreateSeededPort();
		port.SeedRoles(WorkerId, EmployeeRole.Worker, EmployeeRole.RateManager);
		var sut = CreateSut(port);

		var result = await sut.RevokeRoleAsync(new() {
			Context = ContextFor(AdministratorId),
			TargetUserId = WorkerId,
			Role = EmployeeRole.RateManager,
		});

		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
	}

	[Fact]
	public void Revoking_a_role_rejects_none_synchronously()
	{
		var sut = CreateSut(CreateSeededPort());
		var request = new RevokeEmployeeRoleRequest { Context = ContextFor(AdministratorId), TargetUserId = WorkerId, Role = EmployeeRole.None };

		Action act = () => _ = sut.RevokeRoleAsync(request);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task Revoking_an_unheld_role_is_idempotent()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.RevokeRoleAsync(new() {
			Context = ContextFor(AdministratorId),
			TargetUserId = WorkerId,
			Role = EmployeeRole.RateManager,
		});

		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
	}

	[Fact]
	public async Task A_worker_cannot_revoke_a_role()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.RevokeRoleAsync(new() { Context = ContextFor(WorkerId), TargetUserId = WorkerId, Role = EmployeeRole.Worker });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Revoking_a_role_from_a_nonexistent_employee_throws_not_found()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.RevokeRoleAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = new(999), Role = EmployeeRole.Worker });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task An_administrator_can_disable_an_account()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.SetEnabledAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = WorkerId, Enabled = false });

		result.IsEnabled.Should().BeFalse();
	}

	[Fact]
	public async Task Disabling_an_already_disabled_account_is_idempotent()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);
		_ = await sut.SetEnabledAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = WorkerId, Enabled = false });

		var result = await sut.SetEnabledAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = WorkerId, Enabled = false });

		result.IsEnabled.Should().BeFalse();
	}

	[Fact]
	public async Task A_worker_cannot_disable_an_account()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.SetEnabledAsync(new() { Context = ContextFor(WorkerId), TargetUserId = WorkerId, Enabled = false });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Disabling_a_nonexistent_employee_throws_not_found()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.SetEnabledAsync(new() { Context = ContextFor(AdministratorId), TargetUserId = new(999), Enabled = false });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task An_administrator_can_reset_a_password()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.ResetPasswordAsync(new() {
			Context = ContextFor(AdministratorId),
			TargetUserId = WorkerId,
			NewPassword = "correct-horse-battery-staple",
		});

		result.RequiresPasswordChange.Should().BeTrue();
	}

	[Fact]
	public async Task A_worker_cannot_reset_a_password()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.ResetPasswordAsync(new() {
			Context = ContextFor(WorkerId),
			TargetUserId = WorkerId,
			NewPassword = "correct-horse-battery-staple",
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Resetting_a_nonexistent_employees_password_throws_not_found()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.ResetPasswordAsync(new() {
			Context = ContextFor(AdministratorId),
			TargetUserId = new(999),
			NewPassword = "correct-horse-battery-staple",
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_worker_can_set_their_own_home_node()
	{
		var port = CreateSeededPort();
		port.SeedNode(new(10), false);
		var sut = CreateSut(port);

		var result = await sut.SetHomeNodeAsync(new() { Context = ContextFor(WorkerId), NodeId = new JobNodeId(10) });

		result.HomeNodeId.Should().Be(new JobNodeId(10));
	}

	[Fact]
	public async Task Setting_a_home_node_to_a_leaf_throws_invariant_violation()
	{
		var port = CreateSeededPort();
		port.SeedNode(new(10), true);
		var sut = CreateSut(port);

		var act = () => sut.SetHomeNodeAsync(new() { Context = ContextFor(WorkerId), NodeId = new JobNodeId(10) });

		await act.Should().ThrowAsync<InvariantViolationException>();
	}

	[Fact]
	public async Task Setting_a_home_node_to_a_nonexistent_node_throws_not_found()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.SetHomeNodeAsync(new() { Context = ContextFor(WorkerId), NodeId = new JobNodeId(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task SetHomeNodeAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.SetHomeNodeAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_port()
	{
		var act = () => new EmployeeCommands(null!, new PasswordHasher<EmployeeCredentialSubject>());

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_password_hasher()
	{
		var act = () => new EmployeeCommands(CreateSeededPort(), null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task AssignRoleAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.AssignRoleAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task RevokeRoleAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.RevokeRoleAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task SetEnabledAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.SetEnabledAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task ResetPasswordAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.ResetPasswordAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
